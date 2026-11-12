using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Agent
{
    // Create-a-Sim and Buy catalog browsing, to time how long panels take to fill with lots of store content
    // (docs/17 §4 M6, §5). Entry and exit use the game's own calls: the "Edit in CAS" cheat path (Sim.EditSimInCAS:
    // CASLogic.LoadSim + UseTempSimDesc + GameStates.TransitionToCASMode), the CAS close button's discard path
    // (CASPuck.OnCloseClick: RequestClearChangeReport + CASController state None), GameStates.TransitionToBuyMode /
    // TransitionToLiveMode. Category switches call what the tab buttons call. Nothing here saves or commits: CAS edits a
    // temporary copy of the Sim and is always left through the discard path.
    // Every action runs as a game task and returns at once; the task times it (ms until ready and the item count stops
    // changing) and records the result, which the next call and cas_state report.
    public static partial class Handlers
    {
        static void RegisterCas()
        {
            Dispatcher.Register("cas_open", CasOpen);
            Dispatcher.Register("cas_category", CasCategory);
            Dispatcher.Register("cas_close", CasClose);
            Dispatcher.Register("cas_state", CasState);
            Dispatcher.Register("cas_bench", CasBench);
            Dispatcher.Register("buy_open", BuyOpen);
            Dispatcher.Register("buy_category", BuyCategory);
            Dispatcher.Register("buy_close", BuyClose);
        }

        const BindingFlags kPriv = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        delegate bool Ready();
        delegate int Counter();

        static readonly List<Dictionary<string, object>> sUiTimings = new List<Dictionary<string, object>>();
        static volatile bool sUiMeasuring;

        // Runs start() in a game task, then polls every 100 ms until ready() holds and count() (when given) is readable
        // (>= 0) and has not changed for 1.5 s, or timeout. ms = time of the last change seen, i.e. when the panel finished
        // filling; ms/items stay null when ready() never held or the count was never readable (never a made-up 0).
        static object Measure(string label, Work start, Ready ready, Counter count, int timeoutMs)
        {
            if (sUiMeasuring) throw new McpException("previous CAS/Buy action is still being timed; wait and retry");
            Dictionary<string, object> prev;
            lock (sUiTimings) prev = sUiTimings.Count > 0 ? sUiTimings[sUiTimings.Count - 1] : null;
            sUiMeasuring = true;
            Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
            {
                Dictionary<string, object> r = Util.Obj();
                r["action"] = label;
                int t0 = System.Environment.TickCount;
                try
                {
                    start();
                    r["sync_ms"] = System.Environment.TickCount - t0;
                    int last = int.MinValue, lastChange = -1, lastPoll = -1000;
                    bool settled = false;
                    while (System.Environment.TickCount - t0 < timeoutMs)
                    {
                        Simulator.Sleep(0);
                        int now = System.Environment.TickCount - t0;
                        if (now - lastPoll < 100) continue;
                        lastPoll = now;
                        if (!ready()) continue;
                        int n = count != null ? count() : 0;
                        if (n < 0) continue; // count not readable (panel/grid not there): not settled
                        if (n != last) { last = n; lastChange = now; continue; }
                        if (now - lastChange >= 1500) { settled = true; break; }
                    }
                    r["ms"] = lastChange < 0 ? null : (object)lastChange;
                    r["items"] = count == null || last < 0 ? null : (object)last;
                    r["settled"] = settled;
                    if (label.StartsWith("cas_category")) AddFillTimes(r, t0);
                }
                catch (Exception e)
                {
                    Exception x = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                    r["error"] = x.GetType().Name + ": " + x.Message;
                }
                finally
                {
                    r["state"] = SubState();
                    try
                    {
                        if (CasUp())
                        {
                            r["outfit"] = Responder.Instance.CASModel.OutfitCategory.ToString();
                            r["clothing_type"] = CASClothingCategory.CurrentTypeCategory.ToString();
                        }
                    }
                    catch (Exception) { } // diagnostics only; never lose the timing
                    lock (sUiTimings)
                    {
                        sUiTimings.Add(r);
                        if (sUiTimings.Count > 60) sUiTimings.RemoveAt(0);
                    }
                    sUiMeasuring = false;
                }
            }));
            Dictionary<string, object> q = Util.Obj();
            q["queued"] = label;
            q["previous"] = prev;
            return q;
        }

        // ------------------------------------------------------------ CAS panels
        // Each CAS panel is its own layout with an internal static gSingleton (null until loaded, unloaded after its
        // fade-out). Items are read from the panel's own grid: CASHair.mHairTypesGrid (hair parts), CASMakeup.mGridMakeupParts
        // (makeup parts of the chosen type, filled synchronously in Init), CASClothingCategory.mClothingTypesGrid (one
        // CASClothingRow per CAS part, filled a few per tick by ItemGrid.BeginPopulating; done = its mPopulateCallback cleared, polled).

        static object Panel(Type t) { return t.GetField("gSingleton", kPriv).GetValue(null); }

        static bool Shown(Type t) { WindowBase w = Panel(t) as WindowBase; return w != null && w.Visible; }

        static ItemGrid PanelGrid(Type t, string field)
        {
            object p = Panel(t);
            return p == null ? null : t.GetField(field, kPriv).GetValue(p) as ItemGrid;
        }

        // ItemGrid.BeginPopulating keeps its callback until the last item is added
        static bool Filling(ItemGrid g) { return typeof(ItemGrid).GetField("mPopulateCallback", kPriv).GetValue(g) != null; }

        static void SetCasState(CASState s) { typeof(CASController).GetMethod("SetCurrentState", kPriv).Invoke(CASController.Singleton, new object[] { s }); } // internal

        static readonly Type[] kCasPanels = { typeof(CASBasics), typeof(CASPhysical), typeof(CASHair), typeof(CASFacialDetails),
            typeof(CASMakeup), typeof(CASCharacter), typeof(CASClothing), typeof(CASClothingCategory) };

        // CAS state, loaded panels (hidden = loaded but not visible / fading out) and puck busy, for error messages
        static string CasDiag()
        {
            CASState st = CASController.Singleton.CurrentState;
            string s = "cas " + st.mTopState + "/" + st.mMidState + "/" + st.mPhysicalState + "/" + st.mClothingState + "; panels:";
            foreach (Type t in kCasPanels)
            {
                WindowBase w = Panel(t) as WindowBase;
                if (w != null) s += " " + t.Name + (w.Visible ? "" : "(hidden)");
            }
            return s + (CASPuck.Instance != null && CASPuck.Instance.UiBusy ? "; puck busy" : "");
        }

        static void WaitFor(Ready cond, int ms, string what)
        {
            int t0 = System.Environment.TickCount;
            while (!cond())
            {
                if (System.Environment.TickCount - t0 > ms) throw new McpException(what + " not ready after " + ms / 1000 + " s (" + CasDiag() + ")");
                Simulator.Sleep(0);
            }
        }

        static bool CasUp()
        {
            string s = SubState();
            return s != null && s.StartsWith("CAS") && CASPuck.Instance != null && CASController.Singleton != null;
        }

        static bool CasReady() { return CasUp() && CASPuck.Instance.Visible && !Responder.Instance.CASModel.IsProcessing(); }

        static readonly string[] kOutfitNames = { "everyday", "formal", "sleepwear", "swimwear", "athletic", "outerwear" };
        static readonly OutfitCategories[] kOutfits = { OutfitCategories.Everyday, OutfitCategories.Formalwear, OutfitCategories.Sleepwear,
            OutfitCategories.Swimwear, OutfitCategories.Athletic, OutfitCategories.Outerwear };

        static OutfitCategories OutfitByName(string name)
        {
            int i = Array.IndexOf(kOutfitNames, name.Trim().ToLowerInvariant());
            if (i < 0) throw new McpException("unknown outfit '" + name + "'; one of: " + string.Join(", ", kOutfitNames));
            return kOutfits[i];
        }

        // the same refusals as Sim.EditSimInCAS (not in the active household, babies, pregnancy, odd outfits); null = can edit.
        // oc = the outfit CAS will open in (the Sim's current one unless cas_open was given one she has).
        static string CasRefusal(Sim s, OutfitCategories oc)
        {
            SimDescription sd = s.SimDescription;
            if (sd.Household == null || sd.Household != Household.ActiveHousehold) return sd.FullName + " is not in the active household";
            if (!s.IsHuman || sd.Baby || sd.IsPregnant || sd.IsVisuallyPregnant) return sd.FullName + " cannot be edited in CAS (not human, baby or pregnant)";
            if (oc != OutfitCategories.Everyday && oc != OutfitCategories.Formalwear && oc != OutfitCategories.Sleepwear
                && oc != OutfitCategories.Swimwear && oc != OutfitCategories.Athletic && oc != OutfitCategories.Outerwear)
                return sd.FullName + " is wearing " + oc + "; CAS needs an everyday/formal/sleep/swim/athletic/outerwear outfit";
            return null;
        }

        // The outfit CAS opens in: the requested one when the Sim has it (outfit index 0), else what she wears now, else
        // (towel, career, costume...) Everyday: the agent picks the outfit, so a Sim in an odd outfit is no reason to refuse.
        static OutfitCategories CasOutfit(Sim s, OutfitCategories want)
        {
            if (want != OutfitCategories.None && s.SimDescription.GetOutfitCount(want) > 0) return want;
            OutfitCategories cur = s.CurrentOutfitCategory;
            return Array.IndexOf(kOutfits, cur) >= 0 ? cur : OutfitCategories.Everyday;
        }

        // cas_open {sim?, outfit?}: Edit-in-CAS for the named Sim, else the active Sim, else the first household member CAS
        // accepts, also when a named Sim is refused (the action label names the Sim and outfit used); CAS works on a copy.
        // outfit (everyday, formal, ...) opens CAS in that outfit when the Sim has one, instead of the one she is wearing;
        // a Sim in a towel or career outfit opens in Everyday (CasOutfit).
        static object CasOpen(Args a)
        {
            if (SubState() != "LiveModeState") throw new McpException("not in live mode (state " + SubState() + ")");
            OutfitCategories want = a.Has("outfit") ? OutfitByName(a.Str("outfit")) : OutfitCategories.None;
            Sim s = a.Has("sim") ? Util.FindSim(a, "sim") : Util.ActiveSim();
            string why = CasRefusal(s, CasOutfit(s, want)), refused = why;
            if (why != null && Household.ActiveHousehold != null)
                foreach (Sim m in Household.ActiveHousehold.Sims)
                    if (CasRefusal(m, CasOutfit(m, want)) == null) { s = m; why = null; break; }
            if (why != null) throw new McpException(why);
            SimDescription sd = s.SimDescription;
            OutfitCategories oc = CasOutfit(s, want);
            object r = Measure("cas_open " + sd.FullName + " (" + oc + ")", delegate
            {
                CASLogic cas = CASLogic.GetSingleton();
                cas.LoadSim(sd, oc, 0);
                cas.UseTempSimDesc = true;
                GameStates.TransitionToCASMode();
            }, CasReady, null, 180000);
            if (refused != null) ((Dictionary<string, object>)r)["fallback_from"] = refused;
            return r;
        }

        static readonly string[] kCasCategories = { "basics", "body", "hair", "face", "makeup", "personality", "everyday", "formal",
            "sleepwear", "swimwear", "athletic", "outerwear", "tops", "bottoms", "shoes", "outfits", "accessories" };

        // cas_category {category}: presses what a player presses. A different tab goes through the character sheet's own tab
        // click (CASCharacterSheet.SetButtonsAndState -> CASSheetWindow.OnNavigationButtonClick: SetCurrentState, and
        // Activate only when no tab was open; the old panel fades out and its fade-finished handler loads and activates the
        // new one). The old code also called Activate(true) on every switch, which re-showed the fading-out panel, so the
        // chain never reached the new one: makeup/everyday/formal read 0 items and accessories found no clothing panel.
        // Inside a tab: hair/face/makeup = CASPhysical's and CASFacialDetails' own buttons (SetCurrentState + Activate(true),
        // safe there because the sub-panel is not fading), outfit = CASClothing.OnClothingCategoryButtonClick, clothing type =
        // CASClothingCategory.SetTypeCategory (the type row). tops/bottoms/shoes/outfits/accessories keep the current outfit.
        static object CasCategory(Args a)
        {
            if (!CasUp()) throw new McpException("CAS is not open (state " + SubState() + ")");
            string c = a.Str("category").Trim().ToLowerInvariant();
            CASMidState mid = CASMidState.Clothing;
            CASPhysicalState phys = CASPhysicalState.None;
            OutfitCategories outfit = OutfitCategories.None;
            string outfitButton = null, type = null, gridField = "mClothingTypesGrid";
            Type tabPanel = typeof(CASClothingCategory), panel = typeof(CASClothingCategory);
            switch (c)
            {
                case "basics": case "body": mid = CASMidState.Basics; tabPanel = panel = typeof(CASBasics); gridField = null; break;
                case "hair": mid = CASMidState.Physical; phys = CASPhysicalState.Hair; tabPanel = typeof(CASPhysical); panel = typeof(CASHair); gridField = "mHairTypesGrid"; break;
                case "face": mid = CASMidState.FacialDetails; phys = CASPhysicalState.FacialDetailsSummary; tabPanel = panel = typeof(CASFacialDetails); gridField = null; break;
                case "makeup": mid = CASMidState.FacialDetails; phys = CASPhysicalState.Makeup; tabPanel = typeof(CASFacialDetails); panel = typeof(CASMakeup); gridField = "mGridMakeupParts"; break;
                case "personality": mid = CASMidState.Character; tabPanel = panel = typeof(CASCharacter); gridField = null; break;
                case "clothing": case "everyday": outfit = OutfitCategories.Everyday; outfitButton = "mEverydayButton"; break;
                case "formal": outfit = OutfitCategories.Formalwear; outfitButton = "mFormalButton"; break;
                case "sleepwear": outfit = OutfitCategories.Sleepwear; outfitButton = "mSleepwearButton"; break;
                case "swimwear": outfit = OutfitCategories.Swimwear; outfitButton = "mSwimwearButton"; break;
                case "athletic": outfit = OutfitCategories.Athletic; outfitButton = "mExerciseButton"; break;
                case "outerwear": outfit = OutfitCategories.Outerwear; outfitButton = "mOuterwearButton"; break;
                case "tops": case "bottoms": case "shoes": case "outfits": case "accessories": type = c; break;
                default: throw new McpException("unknown CAS category '" + c + "'; one of: " + string.Join(", ", kCasCategories));
            }
            bool clothing = mid == CASMidState.Clothing;
            CASClothingCategory.Category typeCat = type == null ? CASClothingCategory.Category.Tops
                : (CASClothingCategory.Category)Enum.Parse(typeof(CASClothingCategory.Category), type, true);
            Counter count = null;
            if (gridField != null) count = delegate { ItemGrid g = PanelGrid(panel, gridField); return g == null ? -1 : g.Count; };
            // Polled state, no one-shot event: the old code waited 20 s inside the action for
            // OnClothingGridFinishedPopulating and threw before the outfit button was ever pressed (so the Sim stayed in
            // Exercise). Ready = CAS idle, panel shown and done fading in (its fade-finished handler starts the first fill),
            // the requested outfit/type in place, the grid not filling; Measure then waits for a stable Count.
            Ready ready = delegate
            {
                if (!CasReady() || !Shown(panel) || CASPuck.Instance.UiBusy) return false;
                if (clothing)
                {
                    CASClothingCategory cc = Panel(typeof(CASClothingCategory)) as CASClothingCategory;
                    if (cc == null || cc.IsFading) return false;
                    if (outfit != OutfitCategories.None && (Responder.Instance.CASModel.OutfitCategory != outfit
                        || CASController.Singleton.CurrentState.mClothingState != CASClothing.CASClothingStateFromOutfitCategory(outfit))) return false;
                }
                ItemGrid g = gridField == null ? null : PanelGrid(panel, gridField);
                return g == null || !Filling(g);
            };
            return Measure("cas_category " + c, delegate
            {
                ICASModel m = Responder.Instance.CASModel;
                CASController ctl = CASController.Singleton;
                Ready idle = delegate { return !CASPuck.Instance.UiBusy && !m.IsProcessing(); };
                WaitFor(idle, 20000, "CAS input");
                bool switched = ctl.CurrentState.mMidState != mid;
                if (switched)
                {
                    CASCharacterSheet sheet = Panel(typeof(CASCharacterSheet)) as CASCharacterSheet;
                    if (sheet == null) throw new McpException("no CAS character sheet (" + CasDiag() + ")");
                    sheet.SetButtonsAndState(new CASState(CASTopState.CreateASim, mid, CASPhysicalState.None, CASClothingState.None));
                }
                // only the tab panel being there and the UI idle; its grid fill is timed by ready()/count, not here
                WaitFor(delegate { return Shown(tabPanel) && idle(); }, 30000, tabPanel.Name);
                if (phys != CASPhysicalState.None && ctl.CurrentState.mPhysicalState != phys)
                {
                    SetCasState(new CASState(CASTopState.CreateASim, mid, phys, CASClothingState.None));
                    ctl.Activate(true);
                }
                if (phys == CASPhysicalState.Hair) ShowHairNotHats(idle);
                if (!clothing) return;
                CASClothingCategory cc = Panel(typeof(CASClothingCategory)) as CASClothingCategory;
                if (cc == null) throw new McpException("no CASClothingCategory panel (" + CasDiag() + ")");
                WaitFor(delegate { return !cc.IsFading; }, 30000, "CASClothingCategory fade-in");
                if (outfit != OutfitCategories.None && m.OutfitCategory != outfit)
                {
                    // what the outfit button does: CASModel.OutfitCategory = outfit, type Tops, state; the model then raises
                    // OnSimOutfitCategoryChanged -> SetCurrentState + Activate -> SetTypeCategory (refill)
                    object cl = Panel(typeof(CASClothing));
                    if (cl == null) throw new McpException("no CASClothing panel (" + CasDiag() + ")");
                    typeof(CASClothing).GetMethod("OnClothingCategoryButtonClick", kPriv).Invoke(cl,
                        new object[] { typeof(CASClothing).GetField(outfitButton, kPriv).GetValue(cl), new UIButtonClickEventArgs() });
                }
                else if (type != null || !switched)
                    // a type change, or a repeat of the current outfit in an open tab: refill so there is a fill to time
                    // (a fresh tab already filled in its fade-in handler)
                    cc.SetTypeCategory(type == null ? CASClothingCategory.CurrentTypeCategory : typeCat);
            }, ready, count, 90000);
        }

        // A Sim whose worn hair part is a hat gets the hair panel on Hats (CASHair.Init and SetState: PartIsHat(worn) ->
        // SetHairTypeCategory(Hat)), so "hair" timed the hats grid (fill_path "hats"). Press the Hair button as a player
        // would (OnHairButtonClick + the two buttons' Selected); the hats fill before it stays in sync_ms.
        static void ShowHairNotHats(Ready idle)
        {
            List<CASPart> worn = Responder.Instance.CASModel.GetWornParts(BodyTypes.Hair);
            if (worn.Count == 0 || !CASHair.PartIsHat(worn[0])) return;
            FieldInfo type = typeof(CASHair).GetField("mHairType", kPriv);
            const uint kHairButton = 98285829u, kHatsButton = 98285830u;
            // set up (on Hats), or already pressed to Hair by an earlier "hair" in the same panel
            WaitFor(delegate
            {
                CASHair p = Panel(typeof(CASHair)) as CASHair;
                return p != null && p.Visible && idle() && ((CASHair.HairType)type.GetValue(p) == CASHair.HairType.Hat
                    || ((Button)p.GetChildByID(kHairButton, true)).Selected);
            }, 30000, "CASHair hair type");
            CASHair h = (CASHair)Panel(typeof(CASHair));
            if ((CASHair.HairType)type.GetValue(h) != CASHair.HairType.Hat) return;
            ((Button)h.GetChildByID(kHatsButton, true)).Selected = false;
            ((Button)h.GetChildByID(kHairButton, true)).Selected = true;
            h.SetHairTypeCategory(CASHair.HairType.Hair);
        }

        // cas_close: the close button's "exit without saving" path, minus its Yes/No dialog; back to live mode.
        static object CasClose(Args a)
        {
            if (!CasUp()) throw new McpException("CAS is not open (state " + SubState() + ")");
            return Measure("cas_close", delegate
            {
                ICASModel m = Responder.Instance.CASModel;
                CASController ctl = CASController.Singleton;
                ctl.AllowCameraMovement(false);
                int t0 = System.Environment.TickCount;
                while (m.IsProcessing() && System.Environment.TickCount - t0 < 30000) Simulator.Sleep(0);
                m.RequestClearChangeReport();
                ctl.Shutdown(); // = SetCurrentState(CASState.None): CASModel.Done -> ClosingDown -> Quit -> GameStates.ReturnFromCAS
            }, delegate { return SubState() == "LiveModeState"; }, null, 180000);
        }

        // cas_state: in-world state, CAS tab, Buy catalog, whether a timing is running, and all recorded timings.
        static object CasState(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            r["state"] = SubState();
            if (CasUp())
            {
                CASState st = CASController.Singleton.CurrentState;
                r["cas"] = st.mTopState + "/" + st.mMidState + "/" + st.mPhysicalState + "/" + st.mClothingState;
                r["cas_ready"] = CasReady();
                r["outfit"] = Responder.Instance.CASModel.OutfitCategory.ToString();
                r["clothing_type"] = CASClothingCategory.CurrentTypeCategory.ToString(); // accessories falls back to tops when none fit
                r["panels"] = CasDiag();
            }
            if (BuyController.Active)
            {
                object c = BuyCtl();
                r["buy_catalog"] = Convert.ToString(c.GetType().GetField("mCurrCatalogType", kPriv).GetValue(c));
                r["buy_items"] = BuyItems();
            }
            r["measuring"] = sUiMeasuring;
            lock (sUiTimings) r["timings"] = new List<Dictionary<string, object>>(sUiTimings);
            return r;
        }

        // fill_* of a cas_category result, from the timing ts3fixes' CAS grid hook records (CasGrid.cs, with casgrid on or
        // off) for the last hair/makeup fill that started during this action: fill_start_ms = press to fill start (tab
        // switch), fill_first_ms = press to the first tiles shown, fill_ms = fill start to the last tile (null when aborted
        // or still running). Absent without ts3fixes or when no hair/makeup fill ran.
        static void AddFillTimes(Dictionary<string, object> r, int t0)
        {
            int[] f = AppDomain.CurrentDomain.GetData("ts3fixes.casfill") as int[];
            if (f == null || f.Length < 5 || f[0] - t0 < 0) return;
            r["fill_start_ms"] = f[0] - t0;
            r["fill_first_ms"] = f[1] < 0 ? null : (object)(f[1] - t0);
            r["fill_ms"] = f[2] < 0 ? null : (object)(f[2] - f[0]);
            r["fill_incremental"] = f[4] == 1;
            if (f.Length < 7) return; // older ts3fixes
            r["fill_path"] = f[5] >= 0 && f[5] < kFillPaths.Length ? kFillPaths[f[5]] : f[5].ToString();
            r["fill_featured"] = f[6] < 0 ? null : (object)f[6];
        }

        // ts3fixes CasGrid kPath*: why a fill took the original path (fill_featured = store tiles the original added first)
        static readonly string[] kFillPaths = { "incremental", "switch_off", "hats", "costume_makeup" };

        // cas_bench: times, over every hair part the CAS Sim can wear, each per-tile call CASHair.PopulateTypesGrid makes
        // (docs/21 section 4), each call type in its own loop, twice (cold, then warm). Needs CAS open; runs as a game
        // task, result in cas_state timings (action cas_bench). ms per call type in total and per part; design_preset
        // runs after an untimed SetCASPart of the same part (it reads the current part); load_xml counts its fallbacks.
        // Leaves ObjectDesigner on the last part, as a grid fill does; layouts are destroyed after each pass.
        static object CasBench(Args a)
        {
            if (!CasUp()) throw new McpException("CAS is not open (state " + SubState() + ")");
            if (sUiMeasuring) throw new McpException("previous CAS/Buy action is still being timed; wait and retry");
            sUiMeasuring = true;
            Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
            {
                Dictionary<string, object> r = Util.Obj();
                r["action"] = "cas_bench";
                try
                {
                    System.Collections.ArrayList parts = Responder.Instance.CASModel.GetVisibleCASParts(BodyTypes.Hair);
                    r["parts"] = parts.Count;
                    r["cold"] = BenchPass(parts);
                    r["warm"] = BenchPass(parts);
                }
                catch (Exception e) { r["error"] = e.GetType().Name + ": " + e.Message; }
                finally
                {
                    lock (sUiTimings) { sUiTimings.Add(r); if (sUiTimings.Count > 60) sUiTimings.RemoveAt(0); }
                    sUiMeasuring = false;
                }
            }));
            Dictionary<string, object> q = Util.Obj();
            q["queued"] = "cas_bench";
            return q;
        }

        delegate void PartCall(CASPart p);

        static Dictionary<string, object> BenchPass(System.Collections.ArrayList parts)
        {
            Dictionary<string, object> r = Util.Obj();
            StopWatch sw = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            int fallbacks = 0;
            float xmlUs = 0;
            ResourceKey layoutKey = ResourceKey.CreateUILayoutKey("GenericCasItem", 0u);
            List<Layout> layouts = new List<Layout>();
            PartCall[] calls = {
                delegate(CASPart p) { CASUtils.PartDataNumPresets(p.Key); },
                delegate(CASPart p) { UIUtils.GetCustomContentType(p.Key); },
                delegate(CASPart p) { ObjectDesigner.SetCASPart(p.Key); },
                null, // design_preset, below
                delegate(CASPart p) { Layout l = UIManager.LoadLayout(layoutKey); l.GetWindowByExportID(1); layouts.Add(l); },
                delegate(CASPart p) { UIManager.GetCASThumbnailImage(new ThumbnailKey(p.Key, -1, (uint)p.BodyType, (uint)p.AgeGenderSpecies, ThumbnailSize.Large)); },
            };
            string[] names = { "num_presets", "content_type", "set_part", "design_preset", "layout", "thumbnail" };
            try
            {
                for (int i = 0; i < calls.Length; i++)
                {
                    float us = 0;
                    foreach (CASPart p in parts)
                    {
                        if (calls[i] == null)
                        {
                            ObjectDesigner.SetCASPart(p.Key);
                            sw.Restart();
                            string xml = ObjectDesigner.GetDesignPreset(ObjectDesigner.GetDesignPresetIndexFromId(ObjectDesigner.DefaultPresetId));
                            sw.Stop();
                            us += sw.GetElapsedTimeFloat();
                            if (string.IsNullOrEmpty(xml))
                            {
                                fallbacks++;
                                sw.Restart();
                                Simulator.LoadXMLString(new ResourceKey(p.Key.InstanceId, 53690476u, p.Key.GroupId));
                                sw.Stop();
                                xmlUs += sw.GetElapsedTimeFloat();
                            }
                            continue;
                        }
                        sw.Restart();
                        calls[i](p);
                        sw.Stop();
                        us += sw.GetElapsedTimeFloat();
                    }
                    r[names[i] + "_ms"] = Math.Round(us / 1000.0, 1);
                    r[names[i] + "_us_per_part"] = parts.Count == 0 ? 0 : Math.Round(us / parts.Count, 0);
                }
                r["load_xml_ms"] = Math.Round(xmlUs / 1000.0, 1);
                r["load_xml_fallbacks"] = fallbacks;
            }
            finally
            {
                foreach (Layout l in layouts) { try { l.Shutdown(); l.Dispose(); } catch (Exception) { } }
                sw.Dispose();
            }
            return r;
        }

        // ------------------------------------------------------------ Buy

        static object BuyCtl() { return typeof(BuyController).GetField("sController", kPriv).GetValue(null); }

        static int BuyItems()
        {
            object c = BuyCtl();
            ItemGrid g = c == null ? null : c.GetType().GetField("mCatalogGrid", kPriv).GetValue(c) as ItemGrid;
            return g == null ? 0 : g.Count;
        }

        static bool BuyUp() { return SubState() == "BuyModeState" && BuyController.Active && BuyCtl() != null; }

        // buy_open: Buy mode on the active household's lot (what the Buy button does).
        static object BuyOpen(Args a)
        {
            if (SubState() != "LiveModeState") throw new McpException("not in live mode (state " + SubState() + ")");
            Lot lot = HomeLot();
            if (lot == null) throw new McpException("no home lot for the active household");
            return Measure("buy_open", delegate { GameStates.TransitionToBuyMode(lot, null); }, BuyUp, BuyItems, 180000);
        }

        static void FindTagged(WindowBase w, string tagType, List<Button> found, int depth)
        {
            if (w == null || depth > 24) return;
            Button b = w as Button;
            if (b != null && b.Tag != null && b.Tag.GetType().Name == tagType && b.Tag.GetType().DeclaringType == typeof(BuyController)) found.Add(b);
            for (uint i = 0; i < 500; i++)
            {
                WindowBase child;
                try { child = w.GetChildByIndex(i); } catch (Exception) { break; }
                if (child == null) break;
                FindTagged(child, tagType, found, depth + 1);
            }
        }

        // buy_category {category}: a by-function category (Comfort, Surfaces, Decor, ...: the game's internal or
        // localized name, exact or contained). Switches the catalog to by-function first if needed; then calls the
        // category button's own click handler (BuyController.OnCategoryButtonClick -> OnSelectCategory).
        static object BuyCategory(Args a)
        {
            if (!BuyUp()) throw new McpException("Buy mode is not open (state " + SubState() + ")");
            if (sUiMeasuring) throw new McpException("previous CAS/Buy action is still being timed; wait and retry");
            string want = a.Str("category").Trim();
            object c = BuyCtl();
            Type t = typeof(BuyController);
            // "@back": the catalog back button's own handler; "@catalog:<name>": a catalog button (by function, by room,
            // collections, store, inventory: BuyController.Catalog name, exact or contained) through OnCatalogButtonClick.
            // These reach ShowCatalogGrid(false) and SelectCatalog (docs/545 buyclearrecycle, buygridsweep).
            if (want == "@back")
                return Measure("buy_category @back", delegate
                {
                    t.GetMethod("OnCatalogBackButtonClick", kPriv).Invoke(c, new object[] { null, null });
                }, BuyUp, BuyItems, 60000);
            if (want.StartsWith("@catalog:"))
            {
                string cw = want.Substring(9);
                List<Button> cbs = new List<Button>();
                FindTagged((WindowBase)c, "Catalog", cbs, 0);
                Button ch = null; List<string> cn = new List<string>();
                foreach (Button b in cbs)
                {
                    Type ct = b.Tag.GetType();
                    string name = ct.GetField("mName").GetValue(b.Tag) as string ?? "";
                    string loc = ct.GetField("mLocalizedName").GetValue(b.Tag) as string ?? "";
                    cn.Add(name);
                    if (ch == null && (name.IndexOf(cw, StringComparison.OrdinalIgnoreCase) >= 0 || loc.IndexOf(cw, StringComparison.OrdinalIgnoreCase) >= 0)) ch = b;
                }
                if (ch == null) throw new McpException("no buy catalog '" + cw + "'; found: " + string.Join(", ", cn.ToArray()));
                Button hitC = ch;
                return Measure("buy_category " + want, delegate
                {
                    t.GetMethod("OnCatalogButtonClick", kPriv).Invoke(c, new object[] { hitC, null });
                }, BuyUp, BuyItems, 60000);
            }
            if (Convert.ToString(t.GetField("mCurrCatalogType", kPriv).GetValue(c)) != "ByCategory")
            {
                Button byFunction = t.GetField("mCatalogCategoryModeButton", kPriv).GetValue(c) as Button;
                if (byFunction == null) throw new McpException("no by-function catalog button");
                t.GetMethod("OnCatalogButtonClick", kPriv).Invoke(c, new object[] { byFunction, null });
            }
            List<Button> buttons = new List<Button>();
            FindTagged((WindowBase)c, "Category", buttons, 0);
            Button hit = null, partial = null;
            List<string> names = new List<string>();
            foreach (Button b in buttons)
            {
                Type ct = b.Tag.GetType();
                string name = ct.GetField("mName").GetValue(b.Tag) as string ?? "";
                string loc = ct.GetField("mLocalizedName").GetValue(b.Tag) as string ?? "";
                names.Add(name);
                if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase) || string.Equals(loc, want, StringComparison.OrdinalIgnoreCase)) hit = b;
                else if (partial == null && (name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0 || loc.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)) partial = b;
            }
            hit = hit ?? partial;
            if (hit == null) throw new McpException("no buy category '" + want + "'; found: " + string.Join(", ", names.ToArray()));
            return Measure("buy_category " + want, delegate
            {
                t.GetMethod("OnCategoryButtonClick", kPriv).Invoke(c, new object[] { hit, null });
            }, BuyUp, BuyItems, 60000);
        }

        // buy_close: back to live mode (nothing bought, so nothing to undo).
        static object BuyClose(Args a)
        {
            if (!BuyUp()) throw new McpException("Buy mode is not open (state " + SubState() + ")");
            return Measure("buy_close", delegate { GameStates.TransitionToLiveMode(); }, delegate { return SubState() == "LiveModeState"; }, null, 180000);
        }
    }
}
