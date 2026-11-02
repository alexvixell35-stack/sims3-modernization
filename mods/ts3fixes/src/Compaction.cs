using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;
using Sims3.UI;
using Sims3.UI.GameEntry;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // Compaction (docs/204): a quiet save-reload when the game is close to Error 12. ts3native decides ([Compaction] in
    // ts3native.ini, compaction.cpp: threshold on the largest free address-space block, hysteresis, Apex, "did it help") and
    // logs; this file acts. Both off unless [Compaction] Enabled=1 (opt-in: compact after a low save) or 2 (offered: ask).
    //   1. Save: OptionsModel.SaveGameTask (Save, Save As, async autosaves; synchronous saves are always followed by EA's own
    //      quit, travel or move) runs as usual; after it, if the state allows it (live mode, no travel / vacation / university
    //      / other town, no dialog, no emergency), native op 7 gives the verdict (its own walk, save result 1 required).
    //   2. Record (memory only, never in the save: the process survives the reload): game speed, open HUD panel, selected Sim,
    //      camera, and the queues of the Sims the player can see or would notice (CompactionCore.InScope), max 8 entries each,
    //      only entries whose definition is a static Singleton field (read again at the restore: a mod may replace it at load)
    //      and whose target is a Sim, a lot or an object (no terrain points, picker answers or pie-menu paths).
    //   3. EA's own save loading screen comes up at once with "Compacting... (your game is saved)" in the household name line,
    //      then EA's in-game Load (GameStates.TransitionToGameStateReloadWorld, Route=0) or Quit to Main Menu + load from the
    //      MainMenuState start (Route=1; EA's return-to-menu statics clear runs).
    //   4. After the load, one sim tick later: camera (only when off by > 2 m), selected Sim, queues (the game's own Test, then
    //      InteractionQueue.Add; the running head restarts), panel, game speed last; one notification with the gain.
    // Any failure before the transition takes the screen down, plays on and turns Compaction off for the session.
    // Counters [427-438]: saves seen, compactions started, offers, accepted, declined, refused by state, skipped / failed, queue
    // entries recorded, re-pushed, not pushed (Test failed, target or Sim gone, queue refused), not restorable at record,
    // pushes that threw; [439] restores finished.
    public static partial class Fixes
    {
        sealed class CpItem
        {
            public FieldInfo Def;           // the definition's static Singleton field
            public int Kind;                // 0 Sim, 1 object, 2 lot
            public ulong Id;                // SimDescriptionId / the object's lot id / LotId
            public Type ObjType;
            public Vector3 Pos;
            public InteractionPriority Priority;
            public bool Autonomous, Cancellable;
        }
        sealed class CpSim { public ulong SimId; public readonly List<CpItem> Items = new List<CpItem>(); }
        sealed class CpRecord
        {
            public string File;
            public int Tick;
            public Sims3.SimIFace.Gameflow.GameSpeed Speed = Sims3.SimIFace.Gameflow.GameSpeed.Normal;
            public InfoState Panel = InfoState.None;
            public ulong Selected;
            public Vector3 CamPos, CamTarget;
            public bool HasCamera;
            public readonly List<CpSim> Sims = new List<CpSim>();
        }

        // native op 7 skip / fail reasons (compaction.cpp kCpWhy)
        const int kCpWhyState = 1, kCpWhyNoDetails = 2, kCpWhyThrew = 3, kCpWhyLoad = 4;

        static bool sCpMenuHook;
        static CpRecord sCpPending;           // set at the transition, taken by the next world load
        static SaveGameMetadata sCpMenuMeta;  // Route=1: what the MainMenuState start loads
        static int sCpLoadStart, sCpLastLoadMs;
        static readonly Dictionary<Type, FieldInfo> sCpSingletons = new Dictionary<Type, FieldInfo>();

        static bool CpAsk(uint lo) { try { return DiagSend(7, lo); } catch (Exception) { return false; } }

        static int CpValue(int field)
        {
            bool[] bits = new bool[16];
            for (int b = 0; b < 16; b++) bits[b] = CpAsk(0x10000u | (uint)field << 5 | (uint)b);
            return CompactionCore.FromBits(bits);
        }

        static void ApplyCompaction()
        {
            if (!CpAsk(8)) return;  // [Compaction] off, [UserDiag] off or no ts3native: no hooks at all
            if (!Patch(typeof(Sims3.Gameplay.UI.OptionsModel), "SaveGameTask", new Type[] { typeof(object) }, typeof(CompactSaveTaskHook), "TaskHook", "TaskOrig")) return;
            sCpMenuHook = Patch(typeof(MainMenuState), "Startup", Type.EmptyTypes, typeof(CompactMenuHook), "StartupHook", "StartupOrig");
            World.OnWorldLoadStartedEventHandler += delegate { sCpLoadStart = System.Environment.TickCount; };
        }

        public class CompactSaveTaskHook
        {
            public void TaskHook(object obj)
            {
                CpAsk(7);  // the verdict counts only a save that ends after this
                TaskOrig(obj);
                try { CompactAfterSave(); } catch (Exception) { }
            }
            public void TaskOrig(object obj) { } // OptionsModel.SaveGameTask(object)
        }

        public class CompactMenuHook : MainMenuState
        {
            public void StartupHook()
            {
                StartupOrig();
                try { CompactMenuStartup(); } catch (Exception) { }
            }
            public void StartupOrig() { } // MainMenuState.Startup
        }

        // EA refuses Quit to Main Menu in the same states; a travel, vacation or university world reloads differently
        static bool CpStateOk()
        {
            if (!GameStates.IsInWorld() || !GameStates.IsLiveState || GameStates.IsTravelling || GameStates.HasTravelData || GameStates.IsOnVacation
                || GameStates.IsEditingOtherTown || GameStates.MovingWorldName != null || Sims3.SimIFace.GameUtils.IsUniversityWorld()
                || Sims3.SimIFace.GameUtils.IsFutureWorld())
                return false;
            if (UIManager.GetModalWindow() != null) return false;
            string emergency = null;
            return !Sims3.Gameplay.UI.Responder.Instance.HudModel.CheckForEmergency(ref emergency);
        }

        static void CpNotify(string text)
        {
            try { StyledNotification.Show(new StyledNotification.Format(text, StyledNotification.NotificationStyle.kSystemMessage)); } catch (Exception) { }
        }

        static void CpFail(int why)
        {
            PatchCounters()[433]++;
            CpAsk(6u | (uint)why << 8);
            if (why != kCpWhyLoad) CpNotify(CompactionCore.kSkippedText);
        }

        static void CompactAfterSave()
        {
            int[] c = PatchCounters();
            c[427]++;
            if (!CpStateOk()) { c[432]++; return; }  // the verdict is not asked: the arm stays for the next save
            if (!CpAsk(0))
            {
                if (CpAsk(1)) CpNotify(CompactionCore.kStillLowText);
                return;
            }
            if (CpAsk(2))
            {
                c[429]++;
                if (!TwoButtonDialog.Show(CompactionCore.PromptText(CompactionCore.EstimateSeconds(sCpLastLoadMs)), "Compact", "Not now"))
                {
                    c[431]++;
                    CpAsk(9);
                    return;
                }
                c[430]++;
                if (!CpStateOk()) { CpFail(kCpWhyState); return; }
            }
            CompactGo();
        }

        static void CompactGo()
        {
            int[] c = PatchCounters();
            string file = GameStates.LoadFileName;
            SaveGameMetadata meta = new SaveGameMetadata();
            meta.mSaveFile = file;
            if (string.IsNullOrEmpty(file) || !Sims3.Gameplay.UI.Responder.Instance.MainMenuModel.GetSaveFileDetails(ref meta)) { CpFail(kCpWhyNoDetails); return; }
            meta.mHouseholdName = CompactionCore.kLoadingText;  // the loading screen's household name line (fresh metadata object)
            bool menu = CpAsk(3);
            if (menu && !sCpMenuHook)
            {
                DiagLine("compaction: main menu route wanted but the MainMenuState hook is missing: direct route");
                menu = false;
            }
            CpRecord r = CpRecordNow(file);
            CpAsk(4);
            c[428]++;
            sCpPending = r;
            bool screen = false;
            try
            {
                Sims3.SimIFace.GameUtils.EnableSceneDraw(false);  // as EA's travel start: screen first, then the world goes
                LoadingScreenController.LoadSaveGameLoadingScreen(meta);
                screen = true;
                Simulator.Sleep(0);
                if (ShortcutOn("compactionfail")) throw new InvalidOperationException("compactionfail test switch");  // repro compaction-abort
                if (menu)
                {
                    sCpMenuMeta = meta;
                    GameStates.TransitionToLeaveInWorld();
                }
                else GameStates.TransitionToGameStateReloadWorld(file, meta, InWorldState.SubState.LiveMode, true);
            }
            catch (Exception)
            {
                sCpPending = null;
                sCpMenuMeta = null;
                try { if (screen) LoadingScreenController.UnloadNow(); } catch (Exception) { }
                try { Sims3.SimIFace.GameUtils.EnableSceneDraw(true); } catch (Exception) { }
                CpFail(kCpWhyThrew);
            }
        }

        static void CompactMenuStartup()  // MainMenuState.Startup, Route=1: EA's -worldFile start does the same from here
        {
            SaveGameMetadata meta = sCpMenuMeta;
            sCpMenuMeta = null;
            if (meta == null || sCpPending == null) return;
            GameStates.TransitionToGameStateToInWorld(meta.mSaveFile, meta, InWorldState.SubState.LiveMode, true);
        }

        // From Instantiator's world-load handler (after DiagWorldLoad).
        public static void CompactionWorldLoad()
        {
            if (sCpLoadStart != 0) { sCpLastLoadMs = System.Environment.TickCount - sCpLoadStart; sCpLoadStart = 0; }
            CpRecord r = sCpPending;
            sCpPending = null;
            sCpMenuMeta = null;
            if (r == null) return;
            if (!string.Equals(GameStates.LoadFileName, r.File, StringComparison.OrdinalIgnoreCase) || System.Environment.TickCount - r.Tick > CompactionCore.kRestoreMaxAgeMs)
            {
                CpFail(kCpWhyLoad);
                return;
            }
            Simulator.AddObject(new LoopTask(delegate { CompactRestore(r); }));
        }

        static FieldInfo CpSingleton(InteractionDefinition def)
        {
            Type t = def.GetType();
            FieldInfo f;
            if (!sCpSingletons.TryGetValue(t, out f))
            {
                const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (Type owner in new Type[] { t.DeclaringType, t })
                {
                    if (owner == null || f != null) continue;
                    foreach (FieldInfo fi in owner.GetFields(st))
                        if (typeof(InteractionDefinition).IsAssignableFrom(fi.FieldType) && ReferenceEquals(fi.GetValue(null), def)) { f = fi; break; }
                }
                sCpSingletons[t] = f;
            }
            return f != null && ReferenceEquals(f.GetValue(null), def) ? f : null;  // an extra instance of a singleton type: not ours to rebuild
        }

        static CpItem CpItemFor(Sim sim, InteractionInstance ii)
        {
            if (ii == null || ii is IImmediateInteraction) return null;
            InteractionDefinition def = ii.InteractionDefinition;
            if (def == null) return null;
            if (ii.SelectedObjects != null && ii.SelectedObjects.Count > 0) return null;  // picker answers
            string[] path = null;
            try { path = def.GetPath(sim.IsFemale); } catch (Exception) { }
            if (path != null && path.Length > 0) return null;  // pie-menu path (§5: not restorable)
            FieldInfo f = CpSingleton(def);
            if (f == null) return null;
            CpItem it = new CpItem();
            it.Def = f;
            it.Priority = ii.GetPriority();
            it.Autonomous = ii.Autonomous;
            it.Cancellable = ii.CancellableByPlayer;
            IGameObject t = ii.Target;
            Sim ts = t as Sim;
            Lot tl = t as Lot;
            GameObject to = t as GameObject;
            if (ts != null)
            {
                if (ts.SimDescription == null) return null;
                it.Kind = 0;
                it.Id = ts.SimDescription.SimDescriptionId;
            }
            else if (tl != null) { it.Kind = 2; it.Id = tl.LotId; }
            else if (to != null && !(to is Terrain) && to.InWorld)
            {
                it.Kind = 1;
                it.ObjType = to.GetType();
                it.Id = to.LotCurrent != null ? to.LotCurrent.LotId : 0;
                it.Pos = to.Position;
            }
            else return null;
            return it;
        }

        static CpRecord CpRecordNow(string file)
        {
            int[] c = PatchCounters();
            CpRecord r = new CpRecord();
            r.File = file;
            r.Tick = System.Environment.TickCount;
            try { r.Speed = Sims3.Gameplay.UI.Responder.Instance.ClockSpeedModel.CurrentGameSpeed; } catch (Exception) { }
            try { r.Panel = HudController.InfoState; } catch (Exception) { }
            try { r.CamPos = CameraController.GetPosition(); r.CamTarget = CameraController.GetTarget(); r.HasCamera = true; } catch (Exception) { }
            Sim sel = PlumbBob.SelectedActor;
            if (sel != null && sel.SimDescription != null) r.Selected = sel.SimDescription.SimDescriptionId;
            Lot active = LotManager.ActiveLot;
            Household hh = Household.ActiveHousehold;
            foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                try
                {
                    if (sim == null || sim.HasBeenDestroyed || !sim.InWorld || sim.SimDescription == null || sim.InteractionQueue == null) continue;
                    float d = r.HasCamera ? (sim.Position - r.CamTarget).Length() : float.MaxValue;
                    if (!CompactionCore.InScope(hh != null && sim.Household == hh, active != null && sim.LotCurrent == active, d)) continue;
                    CpSim s = new CpSim();
                    s.SimId = sim.SimDescription.SimDescriptionId;
                    int n = 0;
                    foreach (InteractionInstance ii in sim.InteractionQueue.InteractionList)
                    {
                        if (n++ >= CompactionCore.kQueueMax) break;
                        CpItem it = CpItemFor(sim, ii);
                        if (it == null) { c[437]++; continue; }
                        s.Items.Add(it);
                        c[434]++;
                    }
                    if (s.Items.Count > 0) r.Sims.Add(s);
                }
                catch (Exception) { }
            }
            return r;
        }

        static Sim CpFindSim(ulong id)
        {
            SimDescription d = id != 0 ? SimDescription.Find(id) : null;
            return d != null ? d.CreatedSim : null;
        }

        static GameObjectHit CpHit(GameObject o)
        {
            GameObjectHit h = new GameObjectHit(GameObjectHitType.Object);
            h.mPoint = o.Position;
            h.mId = o.ObjectId.Value;
            return h;
        }

        static GameObject CpFindObject(CpItem it)
        {
            List<GameObject> cand = new List<GameObject>();
            List<float> dist = new List<float>();
            foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>(it.Pos, CompactionCore.kObjectMatchM))
            {
                if (o == null || o.GetType() != it.ObjType || (o.LotCurrent != null ? o.LotCurrent.LotId : 0) != it.Id) continue;
                cand.Add(o);
                dist.Add((o.Position - it.Pos).Length());
            }
            int i = CompactionCore.PickNearest(dist.ToArray(), CompactionCore.kObjectMatchM);
            return i < 0 ? null : cand[i];
        }

        static bool CpPush(Sim sim, CpItem it)
        {
            InteractionDefinition def = it.Def.GetValue(null) as InteractionDefinition;  // the current Singleton
            if (def == null) return false;
            IGameObject target;
            GameObjectHit hit;
            if (it.Kind == 0)
            {
                Sim t = CpFindSim(it.Id);
                if (t == null) return false;
                target = t;
                hit = CpHit(t);
            }
            else if (it.Kind == 2)
            {
                Lot l = LotManager.GetLot(it.Id);
                if (l == null) return false;
                target = l;
                hit = new GameObjectHit(GameObjectHitType.LotTerrain);
                hit.mPoint = l.GetCenterPosition();
                hit.mId = l.LotId;
            }
            else
            {
                GameObject o = CpFindObject(it);
                if (o == null) return false;
                target = o;
                hit = CpHit(o);
            }
            InteractionInstanceParameters p = new InteractionInstanceParameters(new InteractionObjectPair(def, target), sim, it.Priority, it.Autonomous, it.Cancellable, hit);
            GreyedOutTooltipCallback cb = null;
            if (!IUtil.IsPass(def.Test(ref p, ref cb))) return false;
            InteractionInstance inst = def.CreateInstanceFromParameters(ref p);
            return inst != null && sim.InteractionQueue.Add(inst);
        }

        static void CompactRestore(CpRecord r)
        {
            int[] c = PatchCounters();
            Simulator.Sleep(1);  // one sim tick after the world is up, game speed still EA's
            if (r.HasCamera)
                try { if ((CameraController.GetTarget() - r.CamTarget).Length() > CompactionCore.kCameraCheckM) CameraController.SetPositionAndTarget(r.CamPos, r.CamTarget); } catch (Exception) { }
            try
            {
                Sim sel = CpFindSim(r.Selected);
                if (sel != null && PlumbBob.SelectedActor != sel) PlumbBob.ForceSelectActor(sel);
            }
            catch (Exception) { }
            foreach (CpSim s in r.Sims)  // head first: the running interaction restarts from its beginning
            {
                Sim sim = CpFindSim(s.SimId);
                foreach (CpItem it in s.Items)
                {
                    if (sim == null || sim.InteractionQueue == null) { c[436]++; continue; }
                    try { if (CpPush(sim, it)) c[435]++; else c[436]++; }
                    catch (Exception) { c[438]++; }
                }
            }
            try { if (r.Panel != HudController.InfoState) HudController.SetInfoState(r.Panel); } catch (Exception) { }
            bool helped = CpAsk(5);  // native: largest free after the reload, "did it help", the +N min line
            int gain = CpValue(1);
            DiagLine("compaction: restored " + c[435] + " queue entries in total, " + c[436] + " not pushed, " + c[437] + " not restorable, " + c[438] + " threw (session counters)");
            try { Sims3.Gameplay.UI.Responder.Instance.ClockSpeedModel.CurrentGameSpeed = r.Speed; } catch (Exception) { }  // last (§4)
            CpNotify(helped ? CompactionCore.DoneText(gain) : CompactionCore.DoneText(gain) + ". " + CompactionCore.kStillLowText);
            c[439]++;
        }
    }
}
