using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Alchemy;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Insect;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.SimIFace;
using Sims3.Store.Objects;

namespace Ts3Fixes
{
    // docs/336 §9 (LC-6a part 2): deadrefslimuse exact by construction. EA writes a use list directly (ActorsUsingMe.Add
    // instead of AddToUseList) in 17 places [verified decompiled + IL scan, tools/uselist-writers.ts]; each enclosing method
    // is hooked here (PicnicPosture's constructor through its only creator, PicnicBasket.SitDown.Run) and opens a pin for its
    // task (DeadRefsCore.UsePins): the objects it may write, resolved at entry (target, this, a field, the right-hand object),
    // plus every object created in that task while it runs (GameObject.Init hook: the food props, egg, juice cup, chair,
    // terrarium) are stepped by every delta pass until it returns, then noted once more; from then on the slim walk's
    // re-note (non-empty list) keeps them. A pin whose task died without returning is noted once and dropped. Pins are
    // kept whatever the switches (so a switch turned on mid-interaction leaves no gap), hold ids only, are reset at load.
    // [1707] pin opens, [1708] pinned ids stepped by delta passes, [1709] objects created inside a pin, [1710] open pins,
    // [1711] high-water open pins, [1712] pins dropped because their task died, [1713] delta passes that walked every object
    // for the use lists (a pin over its id cap), [1714] pin overflows.
    public static partial class Fixes
    {
        static readonly UsePins sDrPins = new UsePins();
        static bool sDrUseAll, sDrUseAllNext;
        static FieldInfo sDrPinInstrument, sDrPinChair;

        static void ApplyDeadRefPins()
        {
            const BindingFlags nest = BindingFlags.Public | BindingFlags.NonPublic;
            sDrPinInstrument = typeof(Subway).GetNestedType("PlayForTips", nest) != null ? typeof(Subway).GetNestedType("PlayForTips", nest).GetField("mInstrument", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) : null;
            sDrPinChair = typeof(LemonadeStand).GetField("mChair", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Patch(typeof(GameObject), "Init", new Type[] { typeof(bool) }, typeof(DrInitHook), "InitHook", "InitOrig");
            Patch(typeof(PhilosopherStone).GetNestedType("TransmuteGold", nest), "Run", Type.EmptyTypes, typeof(DrPinStoneHook), "RunHook", "RunOrig");
            Patch(typeof(PicnicBasket).GetNestedType("SitDown", nest), "Run", Type.EmptyTypes, typeof(DrPinPicnicHook), "RunHook", "RunOrig");
            Patch(typeof(PortableMp3Player), "StartMusic", new Type[] { typeof(bool) }, typeof(DrPinMp3Hook), "StartHook", "StartOrig");
            Patch(typeof(BonehildaCoffin), "SpawnBonehilda", Type.EmptyTypes, typeof(DrPinBonehildaHook), "SpawnHook", "SpawnOrig");
            Patch(typeof(GnubbField), "GetTeam", new Type[] { typeof(Sim), typeof(bool) }, typeof(DrPinGnubbHook), "TeamHook", "TeamOrig");
            Patch(typeof(Shuffleboard), "GetTeam", new Type[] { typeof(Sim), typeof(bool) }, typeof(DrPinShuffleHook), "TeamHook", "TeamOrig");
            Patch(typeof(Terrarium.RadiationExperiment), "OnEpicSuccess", Type.EmptyTypes, typeof(DrPinTerrariumHook), "EpicHook", "EpicOrig");
            Patch(typeof(Subway).GetNestedType("PlayForTips", nest), "StartPlayingForTips", Type.EmptyTypes, typeof(DrPinSubwayHook), "StartHook", "StartOrig");
            Patch(typeof(ChickenCoopClassic).GetNestedType("HarvestEggs", nest), "Run", Type.EmptyTypes, typeof(DrPinCoopHook), "RunHook", "RunOrig");
            Patch(typeof(FVShoppingRegister).GetNestedType("TendRegister", nest), "Run", Type.EmptyTypes, typeof(DrPinFvHook), "RunHook", "RunOrig");
            Patch(typeof(IceCreamMaker).GetNestedType("MakeDeepFriedIceCreamFritter", nest), "Run", Type.EmptyTypes, typeof(DrPinIceCreamHook), "RunHook", "RunOrig");
            Patch(typeof(LemonadeStand).GetNestedType("GetPsychiatricHelp", nest), "Run", Type.EmptyTypes, typeof(DrPinLemonHelpHook), "RunHook", "RunOrig");
            Patch(typeof(LemonadeStand).GetNestedType("Purchase", nest), "Run", Type.EmptyTypes, typeof(DrPinLemonBuyHook), "RunHook", "RunOrig");
            Patch(typeof(LemonadeStand).GetNestedType("Sell", nest), "Run", Type.EmptyTypes, typeof(DrPinLemonSellHook), "RunHook", "RunOrig");
            Patch(typeof(PlaypenClassic).GetNestedType("InPenIdle", nest), "Run", Type.EmptyTypes, typeof(DrPinPlaypenHook), "RunHook", "RunOrig");
            Patch(typeof(TeppanyakiGrill).GetNestedType("TGCook", nest), "Run", Type.EmptyTypes, typeof(DrPinTeppanyakiHook), "RunHook", "RunOrig");
        }

        static ulong DrObjId(object o) { GameObject g = o as GameObject; return g != null ? g.ObjectId.Value : 0; }
        static ulong DrTargetId(object interaction) { InteractionInstance ii = interaction as InteractionInstance; return ii != null ? DrObjId(ii.Target) : 0; }
        static bool DrTaskDead(ulong t) { return !TaskAlive(new ObjectGuid(t)); }

        // a hooked writer starts: pin what it may write for this task; returns the task to close
        static ulong DrPinOpen(ulong a, ulong b)
        {
            ulong task = 0;
            try
            {
                if (WorldGone()) return 0;
                task = Simulator.CurrentTask.Value;
                sDrPins.Open(task, new ulong[] { a, b });
                int[] c = PatchCounters();
                c[1707]++;
                if ((sDrPins.Opens & 255) == 0) DrPinDropDead(c); // no delta pass may run for long (switch off): prune here too
                DrPinGauges(c);
            }
            catch (Exception) { PatchCounters()[410]++; }
            return task;
        }

        static void DrPinClose(ulong task)
        {
            if (task == 0) return;
            try
            {
                bool all;
                List<ulong> ids = sDrPins.Close(task, out all);
                if (all) sDrUseAllNext = true;
                if (ids != null) foreach (ulong id in ids) DrPinNote(id);
                DrPinGauges(PatchCounters());
            }
            catch (Exception) { PatchCounters()[410]++; }
        }

        // into the use set (the next delta pass steps it) when the slim index is kept; otherwise the next pass walks anyway
        static void DrPinNote(ulong id) { if (id != 0 && DrIdxLive() && sDrIdx.SeedSlim) sDrIdx.NoteUse(id); }

        static void DrPinDropDead(int[] c)
        {
            bool all;
            List<ulong> ids = sDrPins.DropDead(DrTaskDead, out all);
            if (all) sDrUseAllNext = true;
            foreach (ulong id in ids) DrPinNote(id);
            c[1712] = sDrPins.DeadDropped;
        }

        static void DrPinGauges(int[] c)
        {
            c[1709] = sDrPins.Attached; c[1710] = sDrPins.Count; c[1711] = sDrPins.HighPins; c[1712] = sDrPins.DeadDropped; c[1714] = sDrPins.Overflows;
        }

        // DrDeltaBegin, slim: dead pins noted, then every pinned id joins the taken use set; a pin in "all" mode (or one that
        // just closed) makes this pass walk every object for the use lists.
        static void DrPinsIntoPass(int[] c)
        {
            DrPinDropDead(c);
            int before = sDrUseTaken.Count;
            sDrPins.CopyIds(sDrUseTaken);
            c[1708] += sDrUseTaken.Count - before;
            sDrUseAll = sDrUseAllNext || sDrPins.AnyAll;
            sDrUseAllNext = false;
            if (sDrUseAll) c[1713]++;
            DrPinGauges(c);
        }

        // world load (DrIdxLoadReset)
        static void DrPinLoadReset() { sDrPins.Reset(); sDrUseAll = sDrUseAllNext = false; }

        // Every GameObject passes this sealed override once, at creation and at load (postLoad). Created inside an open pin:
        // the writer may put a Sim in its list (a food prop, an egg, a juice cup, a chair). docs/336 §9 part 1 also indexes it
        // per lot (DeadRefsPerLot.cs).
        public class DrInitHook : GameObject
        {
            public ScriptExecuteType InitHook(bool postLoad)
            {
                ScriptExecuteType t = InitOrig(postLoad);
                if (sDrPins.Count > 0) try { sDrPins.Attach(Simulator.CurrentTask.Value, ObjectId.Value); } catch (Exception) { PatchCounters()[410]++; }
                DrPlInit(this);
                SdiUrnInit(this);  // docs/359: urnstones the world gains (SimDescIndex.cs)
                return t;
            }
            public ScriptExecuteType InitOrig(bool postLoad) { return ScriptExecuteType.None; } // GameObject.Init
        }

        // TransmuteGold.Run adds the Sim to the stone's slot object before routing
        public class DrPinStoneHook
        {
            public bool RunHook()
            {
                InteractionInstance ii = (object)this as InteractionInstance;
                ulong slot = 0;
                try { GameObject s = ii != null ? ii.Target as GameObject : null; if (s != null) slot = DrObjId(GameObject.GetObject(Slots.GetContainedObject(s.ObjectId, 2820733094u))); } catch (Exception) { PatchCounters()[410]++; }
                ulong t = DrPinOpen(DrTargetId(ii), slot);
                try { return RunOrig(); } finally { DrPinClose(t); }
            }
            public bool RunOrig() { return false; } // PhilosopherStone.TransmuteGold.Run
        }

        // SitDown.Run is the only creator of PicnicPosture, whose constructor adds the Sim to the blanket's list
        public class DrPinPicnicHook
        {
            public bool RunHook()
            {
                InteractionInstance ii = (object)this as InteractionInstance;
                PicnicBasket b = ii != null ? ii.Target as PicnicBasket : null;
                ulong t = DrPinOpen(DrObjId(b), b != null ? DrObjId(b.Blanket) : 0);
                try { return RunOrig(); } finally { DrPinClose(t); }
            }
            public bool RunOrig() { return false; } // PicnicBasket.SitDown.Run
        }

        public class DrPinMp3Hook
        {
            public void StartHook(bool custom) { ulong t = DrPinOpen(DrObjId(this), 0); try { StartOrig(custom); } finally { DrPinClose(t); } }
            public void StartOrig(bool custom) { } // PortableMp3Player.StartMusic
        }

        public class DrPinBonehildaHook
        {
            public bool SpawnHook() { ulong t = DrPinOpen(DrObjId(this), 0); try { return SpawnOrig(); } finally { DrPinClose(t); } }
            public bool SpawnOrig() { return false; } // BonehildaCoffin.SpawnBonehilda
        }

        public class DrPinGnubbHook
        {
            public object TeamHook(Sim sim, bool createIfNotPlaying) { ulong t = DrPinOpen(DrObjId(this), 0); try { return TeamOrig(sim, createIfNotPlaying); } finally { DrPinClose(t); } }
            public object TeamOrig(Sim sim, bool createIfNotPlaying) { return null; } // GnubbField.GetTeam (returns the private Team)
        }

        public class DrPinShuffleHook
        {
            public object TeamHook(Sim sim, bool createIfNotPlaying) { ulong t = DrPinOpen(DrObjId(this), 0); try { return TeamOrig(sim, createIfNotPlaying); } finally { DrPinClose(t); } }
            public object TeamOrig(Sim sim, bool createIfNotPlaying) { return null; } // Shuffleboard.GetTeam (returns the private Team)
        }

        // the new terrarium is created inside (Init attaches it)
        public class DrPinTerrariumHook
        {
            public void EpicHook() { ulong t = DrPinOpen(DrTargetId(this), 0); try { EpicOrig(); } finally { DrPinClose(t); } }
            public void EpicOrig() { } // Terrarium.RadiationExperiment.OnEpicSuccess
        }

        public class DrPinSubwayHook
        {
            public void StartHook()
            {
                ulong inst = 0;
                try { if (sDrPinInstrument != null) inst = DrObjId(sDrPinInstrument.GetValue(this)); } catch (Exception) { PatchCounters()[410]++; }
                ulong t = DrPinOpen(inst, 0);
                try { StartOrig(); } finally { DrPinClose(t); }
            }
            public void StartOrig() { } // Subway.PlayForTips.StartPlayingForTips
        }

        // the egg prop is created inside (Init attaches it)
        public class DrPinCoopHook
        {
            public bool RunHook() { ulong t = DrPinOpen(DrTargetId(this), 0); try { return RunOrig(); } finally { DrPinClose(t); } }
            public bool RunOrig() { return false; } // ChickenCoopClassic.HarvestEggs.Run
        }

        // the fruit prop is created inside (Init attaches it)
        public class DrPinFvHook
        {
            public bool RunHook() { ulong t = DrPinOpen(DrTargetId(this), 0); try { return RunOrig(); } finally { DrPinClose(t); } }
            public bool RunOrig() { return false; } // FVShoppingRegister.TendRegister.Run
        }

        // writes the serving container in the right hand and the maker
        public class DrPinIceCreamHook
        {
            public bool RunHook()
            {
                InteractionInstance ii = (object)this as InteractionInstance;
                ulong hand = 0;
                try { Sim a = ii != null ? ii.InstanceActor : null; if (a != null) hand = DrObjId(a.GetObjectInRightHand()); } catch (Exception) { PatchCounters()[410]++; }
                ulong t = DrPinOpen(DrTargetId(ii), hand);
                try { return RunOrig(); } finally { DrPinClose(t); }
            }
            public bool RunOrig() { return false; } // IceCreamMaker.MakeDeepFriedIceCreamFritter.Run
        }

        // writes the stand's chair (a new chair is attached by Init)
        public class DrPinLemonHelpHook
        {
            public bool RunHook()
            {
                InteractionInstance ii = (object)this as InteractionInstance;
                ulong chair = 0;
                try { object s = ii != null ? ii.Target : null; if (s != null && sDrPinChair != null) chair = DrObjId(sDrPinChair.GetValue(s)); } catch (Exception) { PatchCounters()[410]++; }
                ulong t = DrPinOpen(DrTargetId(ii), chair);
                try { return RunOrig(); } finally { DrPinClose(t); }
            }
            public bool RunOrig() { return false; } // LemonadeStand.GetPsychiatricHelp.Run
        }

        // the juice cup is created inside (Init attaches it)
        public class DrPinLemonBuyHook
        {
            public bool RunHook() { ulong t = DrPinOpen(DrTargetId(this), 0); try { return RunOrig(); } finally { DrPinClose(t); } }
            public bool RunOrig() { return false; } // LemonadeStand.Purchase.Run
        }

        // the fruit prop is created inside (Init attaches it)
        public class DrPinLemonSellHook
        {
            public bool RunHook() { ulong t = DrPinOpen(DrTargetId(this), 0); try { return RunOrig(); } finally { DrPinClose(t); } }
            public bool RunOrig() { return false; } // LemonadeStand.Sell.Run
        }

        public class DrPinPlaypenHook
        {
            public bool RunHook() { ulong t = DrPinOpen(DrTargetId(this), 0); try { return RunOrig(); } finally { DrPinClose(t); } }
            public bool RunOrig() { return false; } // PlaypenClassic.InPenIdle.Run
        }

        // the bowl prop is created inside (Init attaches it)
        public class DrPinTeppanyakiHook
        {
            public bool RunHook() { ulong t = DrPinOpen(DrTargetId(this), 0); try { return RunOrig(); } finally { DrPinClose(t); } }
            public bool RunOrig() { return false; } // TeppanyakiGrill.TGCook.Run
        }
    }
}
