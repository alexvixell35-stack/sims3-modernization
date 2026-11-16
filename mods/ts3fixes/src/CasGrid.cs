using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;
using Sims3.SimIFace.CustomContent;
using Sims3.UI;
using Sims3.UI.CAS;
using Sims3.UI.Store;

namespace Ts3Fixes
{
    // Create-a-Sim hair and makeup grids (docs/21). CASHair.PopulateTypesGrid and CASMakeup.PopulatePartsGrid build every
    // tile in one call on the sim thread (hair: ~2.2 s for 261 parts with the store set). This fills them through
    // ItemGrid.BeginPopulating, the way EA already fills clothing (CASClothingCategory), but by time: at most
    // kCasTickBudgetUs of tile work per UI tick (BudgetedParts), so the tab switch only pays for the first batch.
    // ItemGrid.Clear/Dispose/BeginPopulating abort a fill in progress, so a category change, a refill or CAS closing
    // (the panel layout is disposed) cancels it. Featured store tiles (hair) still come from the original, run with an
    // empty part list. Hats and costume makeup keep the original. Switch casgrid (tuning kCasGrid). casmemo (same tuning)
    // adds a per-CAS-session cache of each hair part's
    // content type and default preset XML (SetCASPart + GetDesignPreset / LoadXMLString), cleared when CAS closes or new
    // content arrives. Counters: [59] incremental fills started, [60] preset cache hits, [61] preset lookups run.
    // Fill timing for the test agent in AppDomain data ts3fixes.casfill (CasFillTimes), with the switch on or off.
    public static partial class Fixes
    {
        static void ApplyCasGrid()
        {
            Patch(typeof(CASHair), "PopulateTypesGrid", typeof(CasHairHook), "PopulateHook", "PopulateOrig");
            Patch(typeof(CASMakeup), "PopulatePartsGrid", typeof(CasMakeupHook), "PopulateHook", "PopulateOrig");
        }

        const BindingFlags kPrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        const int kCasTilesPerTick = 32; // stride cap per UI tick; the time budget normally ends a tick first
        const uint kCasTickBudgetUs = 8000; // tile work per UI tick (~4-5 tiles at the measured 1.6-2.4 ms LoadLayout)

        // which path a fill took ([5] of ts3fixes.casfill)
        const int kPathIncremental = 0, kPathSwitchOff = 1, kPathHats = 2, kPathCostumeMakeup = 3;

        // [0] fill start (TickCount), [1] first batch shown, [2] done (-1 while filling or when aborted), [3] tiles (-1 =
        // original path), [4] 1 = incremental, [5] path (kPath*), [6] featured store tiles from the original (-1 = n/a)
        static int[] CasFillTimes()
        {
            int[] t = AppDomain.CurrentDomain.GetData("ts3fixes.casfill") as int[];
            if (t == null) { t = new int[7]; AppDomain.CurrentDomain.SetData("ts3fixes.casfill", t); }
            return t;
        }
        static int[] FillStart(int path)
        {
            int[] t = CasFillTimes();
            t[0] = System.Environment.TickCount; t[1] = t[2] = -1; t[3] = -1; t[4] = path == kPathIncremental ? 1 : 0;
            t[5] = path; t[6] = -1;
            if (path == kPathIncremental) PatchCounters()[59]++;
            return t;
        }

        static readonly object kPause = new object(); // a no-work item: the tick's time budget is spent

        // The part list for BeginPopulating, handed out by time. BeginPopulating and its tick handler call MoveNext stride + 1
        // times per tick as long as every callback returns true (the tile callbacks always do), so every (stride + 1)th call
        // starts a tick: the clock restarts there, and once the budget is spent the rest of the tick gets kPause.
        // ItemGrid.SelectByComparableTag enumerates the collection again during a fill: later GetEnumerator calls get the
        // plain list, never the fill's own enumerator.
        class BudgetedParts : IEnumerable
        {
            readonly IList mParts; Ticker mTicker;
            public BudgetedParts(IList parts) { mParts = parts; }
            public IEnumerator GetEnumerator()
            {
                if (mTicker != null) return mParts.GetEnumerator();
                mTicker = new Ticker(mParts.GetEnumerator());
                return mTicker;
            }
        }

        class Ticker : IEnumerator
        {
            readonly IEnumerator mParts;
            readonly StopWatch mWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds); // null when the engine has none
            int mCall, mTickStart;
            object mCurrent;
            public Ticker(IEnumerator parts) { mParts = parts; }
            public object Current { get { return mCurrent; } }
            public void Reset() { }
            uint ElapsedUs() { return mWatch != null ? mWatch.GetElapsedTime() : (uint)(System.Environment.TickCount - mTickStart) * 1000u; }
            public bool MoveNext()
            {
                if (mCall++ % (kCasTilesPerTick + 1) == 0)
                {
                    if (mWatch != null) mWatch.Restart(); else mTickStart = System.Environment.TickCount;
                }
                else if (ElapsedUs() >= kCasTickBudgetUs) { mCurrent = kPause; return true; }
                if (mParts.MoveNext()) { mCurrent = mParts.Current; return true; }
                mCurrent = null;
                if (mWatch != null) mWatch.Dispose(); // shortcut: an aborted fill leaves its watch to the GC, fine at one per fill
                return false;
            }
        }
        static void FillDone(int[] t, int tiles)
        {
            if (!ReferenceEquals(t, CasFillTimes()) ) return;
            int now = System.Environment.TickCount;
            if (t[1] < 0) t[1] = now;
            t[2] = now; t[3] = tiles;
        }

        static CASPart WornPart(BodyTypes body) // = the panels' private GetWornPart (their invalid part is default(CASPart))
        {
            List<CASPart> worn = Responder.Instance.CASModel.GetWornParts(body);
            return worn.Count > 0 ? worn[0] : default(CASPart);
        }

        // ---- per-CAS-session part data (casmemo). Inputs are the part key only: SetCASPart(key) then the default preset
        // of that part, or its preset XML resource; user design presets do not change the default preset (inferred), and
        // hair (not hats) only shows the default one. Age/gender/species change also clears it, to be safe.
        class CasPartMemo { public ResourceKeyContentCategory Type; public string Xml; public bool HasXml; }
        static readonly Dictionary<ResourceKey, CasPartMemo> sPartMemo = new Dictionary<ResourceKey, CasPartMemo>();
        static ICASModel sMemoModel;
        static CASAgeGenderFlags sMemoAgs;
        static void ClearPartMemo() { sPartMemo.Clear(); }

        static CasPartMemo HairPartData(CASPart part, CASAgeGenderFlags ags)
        {
            bool memo = ShortcutOn("casmemo");
            CasPartMemo d = null;
            if (memo)
            {
                ICASModel m = Responder.Instance.CASModel;
                if (!ReferenceEquals(m, sMemoModel))
                {
                    sPartMemo.Clear();
                    sMemoModel = m;
                    m.ClosingDown += ClearPartMemo;
                    m.OnCASContentAdded += ClearPartMemo;
                }
                if (ags != sMemoAgs) { sPartMemo.Clear(); sMemoAgs = ags; }
                sPartMemo.TryGetValue(part.Key, out d);
            }
            if (d == null)
            {
                d = new CasPartMemo();
                d.Type = UIUtils.GetCustomContentType(part.Key);
                if (memo) sPartMemo[part.Key] = d;
            }
            if (d.HasXml) { PatchCounters()[60]++; return d; }
            if (UIUtils.IsContentTypeDisabled(d.Type)) return d; // the original skips these before SetCASPart
            ObjectDesigner.SetCASPart(part.Key);
            string xml = ObjectDesigner.GetDesignPreset(ObjectDesigner.GetDesignPresetIndexFromId(ObjectDesigner.DefaultPresetId));
            if (string.IsNullOrEmpty(xml)) xml = Simulator.LoadXMLString(new ResourceKey(part.Key.InstanceId, 53690476u, part.Key.GroupId));
            d.Xml = xml; d.HasXml = true;
            PatchCounters()[61]++;
            return d;
        }

        // ---- hair
        class HairFill
        {
            public CASHair Panel; public CASPart Worn; public bool Filterable, Found; public int Tiles; public int[] Times;
            public CASAgeGenderFlags Ags;
        }

        static FieldInfo sHairType, sHairGrid, sHairStyles, sHairSort, sHairSave, sHairFilter, sHairParts;
        static MethodInfo sHairAddTile;

        static object HairField(ref FieldInfo f, string name, CASHair p)
        {
            if (f == null) f = typeof(CASHair).GetField(name, kPrivate);
            return f.GetValue(p);
        }

        public class CasHairHook : CASHair
        {
            public CasHairHook(uint h) : base(h) { }

            public void PopulateHook(bool isHat)
            {
                CASHair self = this;
                int path = !ShortcutOn("casgrid") ? kPathSwitchOff
                    : (HairType)HairField(ref sHairType, "mHairType", self) != HairType.Hair ? kPathHats : kPathIncremental;
                if (path != kPathIncremental)
                {
                    int[] t = FillStart(path);
                    PopulateOrig(isHat);
                    FillDone(t, -1);
                    return;
                }
                ItemGrid grid = (ItemGrid)HairField(ref sHairGrid, "mHairTypesGrid", self);
                ICASModel m = Responder.Instance.CASModel;
                ArrayList parts = (ArrayList)HairField(ref sHairParts, "mPartsList", self);
                HairFill fill = new HairFill();
                fill.Times = FillStart(kPathIncremental);
                // The original with an empty part list: clears the grid, adds the featured store tiles (the store catalog
                // offers them without any store content installed; buy buttons, tooltips), sets the filter tags and clears
                // mUndoOnDelete. The parts follow after those tiles, as in the original.
                sHairParts.SetValue(self, new ArrayList());
                try { PopulateOrig(isHat); }
                finally { sHairParts.SetValue(self, parts); }
                fill.Times[6] = grid.Count;
                fill.Panel = self; fill.Worn = WornPart(BodyTypes.Hair);
                fill.Filterable = grid.Tag is bool && (bool)grid.Tag;
                fill.Ags = m.Age | m.Gender | m.Species;
                ResourceKey layout = ResourceKey.CreateUILayoutKey("GenericCasItem", 0u);
                // a copy: LoadParts refills mPartsList in place, which would break the enumerator between ticks
                grid.BeginPopulating(HairTile, new BudgetedParts(new ArrayList(parts)), kCasTilesPerTick, layout, fill);
                if (fill.Times[1] < 0 && ReferenceEquals(fill.Times, CasFillTimes())) fill.Times[1] = System.Environment.TickCount;
            }

            public void PopulateOrig(bool isHat) { } // receives PopulateTypesGrid's original header
        }

        // One part = one tile (hair shows only each part's default preset). Always returns true, also for skipped parts and
        // kPause, so BudgetedParts can count the calls of a tick; current == null is BeginPopulating's end call.
        static bool HairTile(ItemGrid grid, object current, ResourceKey layout, object context)
        {
            HairFill f = (HairFill)context;
            try
            {
                if (current == null) { HairFinish(f, grid); return true; }
                if (current == kPause) return true;
                CASPart part = (CASPart)current;
                bool wardrobe = Responder.Instance.CASModel.ActiveWardrobeContains(part);
                CasPartMemo d = HairPartData(part, f.Ags);
                if (UIUtils.IsContentTypeDisabled(d.Type)) return true;
                CASPartPreset preset = new CASPartPreset(part, d.Xml);
                if (!preset.Valid) return true;
                if (sHairAddTile == null) sHairAddTile = typeof(CASHair).GetMethod("AddHairTypeGridItem", kPrivate);
                object[] args = { grid, layout, preset, wardrobe, f.Filterable };
                bool added = (bool)sHairAddTile.Invoke(f.Panel, args);
                f.Filterable = (bool)args[4];
                if (part.Key == f.Worn.Key)
                {
                    f.Found = true;
                    if (added) grid.SelectedItem = grid.Count - 1; // the original also selected the previous tile when the filter hid the worn part
                }
                if (added) f.Tiles++;
                return true;
            }
            catch (Exception) { return true; } // a throwing UI tick handler would repeat every tick; skip the tile
        }

        // What the original does after its loop (hair, not hats).
        static void HairFinish(HairFill f, ItemGrid grid)
        {
            CASHair p = f.Panel;
            grid.Tag = f.Filterable;
            ItemGrid styles = (ItemGrid)HairField(ref sHairStyles, "mHairStylesGrid", p);
            if (styles.Tag == null) styles.Tag = false;
            ((Button)HairField(ref sHairSort, "mSortButton", p)).Tag = (bool)grid.Tag || (bool)styles.Tag;
            if (f.Found) ((Button)HairField(ref sHairSave, "mSaveButton", p)).Enabled = false;
            ((CatalogProductFilter)HairField(ref sHairFilter, "mContentTypeFilter", p)).UpdateFilterButtonState();
            FillDone(f.Times, f.Tiles);
        }

        // ---- makeup (parts grid of lipstick, eyeshadow, eyeliner, blush; costume makeup rows keep the original)
        class MakeupFill { public CASMakeup Panel; public CASPart Worn; public int Tiles; public int[] Times; }

        static FieldInfo sMakeupGrid, sMakeupColor, sMakeupDelete;
        static MethodInfo sMakeupVisible, sMakeupAddTile;

        static object MakeupField(ref FieldInfo f, string name, CASMakeup p)
        {
            if (f == null) f = typeof(CASMakeup).GetField(name, kPrivate);
            return f.GetValue(p);
        }

        public class CasMakeupHook : CASMakeup
        {
            public CasMakeupHook(uint h) : base(h) { }

            public void PopulateHook(BodyTypes category)
            {
                if (category == BodyTypes.CostumeMakeup || !ShortcutOn("casgrid"))
                {
                    int[] t = FillStart(category == BodyTypes.CostumeMakeup ? kPathCostumeMakeup : kPathSwitchOff);
                    PopulateOrig(category);
                    FillDone(t, -1);
                    return;
                }
                CASMakeup self = this;
                ItemGrid grid = (ItemGrid)MakeupField(ref sMakeupGrid, "mGridMakeupParts", self);
                grid.Clear();
                MakeupFill fill = new MakeupFill();
                fill.Panel = self; fill.Worn = WornPart(category);
                // the original sets these from the selection after its loop; off until the fill ends
                ((Button)MakeupField(ref sMakeupColor, "mButtonColor", self)).Enabled = false;
                ((Button)MakeupField(ref sMakeupDelete, "mButtonDelete", self)).Enabled = false;
                if (sMakeupVisible == null) sMakeupVisible = typeof(CASMakeup).GetMethod("GetVisibleParts", kPrivate);
                List<object> parts = (List<object>)sMakeupVisible.Invoke(self, new object[] { category }); // a new list per call
                fill.Times = FillStart(kPathIncremental);
                grid.BeginPopulating(MakeupTile, new BudgetedParts(parts), kCasTilesPerTick, ResourceKey.CreateUILayoutKey("GenericCasItem", 0u), fill);
                if (fill.Times[1] < 0 && ReferenceEquals(fill.Times, CasFillTimes())) fill.Times[1] = System.Environment.TickCount;
            }

            public void PopulateOrig(BodyTypes category) { } // receives PopulatePartsGrid's original header
        }

        static bool MakeupTile(ItemGrid grid, object current, ResourceKey layout, object context)
        {
            MakeupFill f = (MakeupFill)context;
            try
            {
                if (current == null)
                {
                    bool on = grid.SelectedItem != -1 && grid.SelectedItem != 0;
                    ((Button)MakeupField(ref sMakeupColor, "mButtonColor", f.Panel)).Enabled = on;
                    ((Button)MakeupField(ref sMakeupDelete, "mButtonDelete", f.Panel)).Enabled = on;
                    FillDone(f.Times, f.Tiles);
                    return true;
                }
                if (current == kPause) return true;
                CASPart part = (CASPart)current;
                if (sMakeupAddTile == null) sMakeupAddTile = typeof(CASMakeup).GetMethod("AddPartsGridItem", kPrivate);
                sMakeupAddTile.Invoke(f.Panel, new object[] { grid, layout, part });
                if (part.Key == f.Worn.Key) grid.SelectedItem = grid.Count - 1;
                f.Tiles++;
                return true;
            }
            catch (Exception) { return true; } // true: BudgetedParts counts every call of a tick
        }
    }
}
