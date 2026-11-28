using System;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.TombObjects;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // World Adventures tomb registries (docs/607, s74). Both default off; EA bug fixes as EA wrote them elsewhere.
    public static partial class Fixes
    {
        // ---- docs/607 §2 tspawninvdup (kTreasureSpawnInvDup) ---------------------------------------------------------------
        // [verified, decompiled] TreasureSpawnPoint.SetupEventListening links the spawner to the treasure sitting on it:
        // ObjectDisposed is unsubscribed before it is subscribed (`-=` then `+=`), but the treasure's
        // ItemComp.OnAddToInventoryEvent gets only `+=`. EA calls SetupEventListening from OnStartup (which also runs on
        // every world load, GameObject.Startup), from PlaceTreasure, and again from the static OnTravelComplete pass right
        // after SpawnTreasure. So on every arrival in a vacation world the treasure on each spawner holds two
        // subscriptions (three for one placed by that pass). Picking it up runs OnObjectAddedToInventory once per copy:
        // the spawner's tomb trigger Trigger.RemovedItem fires two or three times (a Toggle or logic-gate link flips back,
        // FireOnce/Arm links run again, kTombObjectTrigger is sent again), and CleanupEventListening's single `-=` leaves
        // the other copies on the item, so the empty pedestal fires RemovedItem again every time that relic is added to an
        // inventory later in the visit. The delegate type is [Persistable(false)], so nothing of this reaches the save.
        // Fix as EA intended: after EA's body, keep one subscription (the `-=` before `+=` EA wrote one line above).
        // Household-visible: the tomb triggers fire once per pickup for everyone, as EA's other trigger paths do.
        // Counters: [3388] extra subscriptions removed, [3389] extra subscriptions seen with the switch off, [3390] errors.
        static FieldInfo sTspSpawned, sTspEvent;
        static MethodInfo sTspHandler;
        static string sTspMissing = "";

        static void ApplyTreasureSpawnInvDup()
        {
            sTspSpawned = typeof(TreasureSpawnPoint).GetField("mSpawnedTreasure", kInstAll);
            sTspEvent = typeof(ItemComponent).GetField("OnAddToInventoryEvent", kInstAll);
            sTspHandler = typeof(TreasureSpawnPoint).GetMethod("OnObjectAddedToInventory", kInstAll);
            Patch(typeof(TreasureSpawnPoint), "SetupEventListening", Type.EmptyTypes, typeof(TspHook), "SetupHook", "SetupOrig");
            sTspMissing = EpLive(null, 0, "mSpawnedTreasure", sTspSpawned, typeof(ITreasureSpawnableObject),
                "OnAddToInventoryEvent", sTspEvent, typeof(ItemComponent.OnAddToInventoryCallback), "OnObjectAddedToInventory", sTspHandler, 2);
        }

        // Pure: subscriptions left after one SetupEventListening call that starts with `before` copies. EA adds one;
        // ours (EA's body, then the extras dropped) leaves one.
        internal static int TspAfterSetup(int before, bool fixedOn)
        {
            int ea = Math.Max(0, before) + 1;
            return fixedOn ? 1 : ea;
        }

        // Pure: copies after a world load (OnStartup) plus `travelPasses` OnTravelComplete passes (SpawnTreasure finds
        // the treasure in place, then SetupEventListening runs once more).
        internal static int TspAfterArrival(int travelPasses, bool fixedOn)
        {
            int n = TspAfterSetup(0, fixedOn);
            for (int i = 0; i < travelPasses; i++) n = TspAfterSetup(n, fixedOn);
            return n;
        }

        // Copies of this spawner's handler on the treasure's add-to-inventory event (live, read-only).
        static int TspCopies(TreasureSpawnPoint sp, ItemComponent ic, out Delegate one)
        {
            one = null;
            Delegate d = sTspEvent.GetValue(ic) as Delegate;
            if (d == null) return 0;
            int n = 0;
            foreach (Delegate x in d.GetInvocationList())
                if (ReferenceEquals(x.Target, sp) && x.Method == sTspHandler) { n++; one = x; }
            return n;
        }

        static void TspDedupe(TreasureSpawnPoint sp)
        {
            if (sTspSpawned == null || sTspEvent == null || sTspHandler == null) return;
            ITreasureSpawnableObject t = sTspSpawned.GetValue(sp) as ITreasureSpawnableObject;
            if (t == null) return;
            ItemComponent ic = t.ItemComp;
            if (ic == null) return;
            Delegate one;
            int n = TspCopies(sp, ic, out one);
            if (n <= 1) return;
            int extra = n - TspAfterSetup(n - 1, ShortcutOn("tspawninvdup"));
            if (extra <= 0) { PatchCounters()[3389] += n - 1; return; }
            ItemComponent.OnAddToInventoryCallback cb = (ItemComponent.OnAddToInventoryCallback)one;
            for (int i = 0; i < extra; i++) ic.OnAddToInventoryEvent -= cb;  // the same target and method: removes one copy each
            PatchCounters()[3388] += extra;
        }

        public abstract class TspHook : TreasureSpawnPoint
        {
            public void SetupHook()
            {
                SetupOrig();
                try { TspDedupe(this); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3390]++; }
            }
            public void SetupOrig() { } // receives TreasureSpawnPoint.SetupEventListening
        }

        // TreasureSpawnInvDupCheck run: copies after a vacation arrival and after a placed treasure, EA vs ours (pure),
        // plus the swap and members live. Controls: a spawner set up once (a fresh world without travel) has one copy
        // either way; EA's arrival does leave two (the bug is real).
        public static object TreasureSpawnInvDupCheck(string what)
        {
            string counters = B582Counters("tspawninvdup", 3388, new[] { "extra subscriptions removed", "extras seen with switch off", "errors" }, sTspMissing);
            if (what == "result") return counters;
            if (what != "run") return "tspawninvdup <run|result>";
            bool on = ShortcutOn("tspawninvdup");
            bool fixedPure = false, controls = false;
            string ctl = "", missing = sTspMissing;
            try
            {
                bool eaBug = TspAfterArrival(1, false) == 2 && TspAfterArrival(2, false) == 3;
                fixedPure = on && TspAfterArrival(1, on) == 1 && TspAfterArrival(2, on) == 1 && TspAfterSetup(2, on) == 1;
                controls = eaBug && TspAfterArrival(0, false) == 1 && TspAfterArrival(0, true) == 1 && TspAfterSetup(0, true) == 1;
                ctl = "EA's arrival leaves 2 copies (" + eaBug + "), one setup leaves 1 both ways";
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            return B582Line("tspawninvdup", on, "one add-to-inventory subscription per spawned treasure", fixedPure && sTspMissing.Length == 0, ctl, controls, missing, counters);
        }

        // ---- docs/607 §3 sarcmummynpc (kSarcMummyNpc) ----------------------------------------------------------------------
        // [verified, decompiled] Sarcophagus.TombMummyCreate adds the tomb mummy's SimDescription to Household.NpcHousehold
        // and TombMummyDestroy takes it out again before the Sim fades out. Sarcophagus.CommonCleanup (Dispose and DoReset)
        // only calls mTombMummy.Destroy(): Sim.Dispose clears SimDescription.CreatedSim and leaves the description where it
        // is, so every reset or deletion of a sarcophagus while its mummy is out leaves a Sim-less elder mummy in the NPC
        // household for good (saved with the vacation world); mTombMummysTarget also keeps the Sim it was following.
        // Fix as EA intended (TombMummyDestroy): after EA's cleanup, the description that was the live tomb mummy's is
        // removed from NpcHousehold and the follow target cleared. Skipped while the game shuts down or changes worlds
        // (the world is going away), and only for a description still in NpcHousehold (never a household Sim).
        // Counters: [3391] mummy descriptions removed, [3392] left in NpcHousehold with the switch off, [3393] errors.
        static FieldInfo sSmMummy, sSmTarget;
        static string sSmMissing = "";

        static void ApplySarcMummyNpc()
        {
            sSmMummy = typeof(Sarcophagus).GetField("mTombMummy", kInstAll);
            sSmTarget = typeof(Sarcophagus).GetField("mTombMummysTarget", kInstAll);
            Patch(typeof(Sarcophagus), "CommonCleanup", Type.EmptyTypes, typeof(SmHook), "CleanupHook", "CleanupOrig");
            sSmMissing = EpLive(null, 0, "mTombMummy", sSmMummy, typeof(Sim), "mTombMummysTarget", sSmTarget, typeof(Sim));
        }

        // Pure: is the cleaned-up mummy's description taken out of the NPC household?
        internal static bool SmRemove(bool hadMummy, bool inNpcHousehold, bool worldGoing, bool fixedOn)
        {
            return fixedOn && hadMummy && inNpcHousehold && !worldGoing;
        }

        public abstract class SmHook : Sarcophagus
        {
            public void CleanupHook()
            {
                SimDescription d = null;
                try { Sim m = sSmMummy != null ? sSmMummy.GetValue(this) as Sim : null; if (m != null) d = m.SimDescription; }
                catch (Exception) { PatchCounters()[3393]++; }
                CleanupOrig();
                if (d == null) return;
                try
                {
                    Household npc = Household.NpcHousehold;
                    bool inNpc = npc != null && d.Household == npc;
                    bool going = GameStates.IsGameShuttingDown || GameStates.IsTravelling;
                    if (!SmRemove(true, inNpc, going, ShortcutOn("sarcmummynpc")))
                    {
                        if (inNpc && !going) PatchCounters()[3392]++;
                        return;
                    }
                    npc.Remove(d);
                    if (sSmTarget != null) sSmTarget.SetValue(this, null);
                    PatchCounters()[3391]++;
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3393]++; }
            }
            public void CleanupOrig() { } // receives Sarcophagus.CommonCleanup
        }

        // SarcMummyNpcCheck run: the reset/dispose path removes the mummy's NPC description (pure), plus the swap and
        // members live. Controls: no mummy out, a description no longer in NpcHousehold, and a world that is going away
        // are left as EA leaves them; EA's path (off) never removes it (the bug is real).
        public static object SarcMummyNpcCheck(string what)
        {
            string counters = B582Counters("sarcmummynpc", 3391, new[] { "mummy descriptions removed", "left with switch off", "errors" }, sSmMissing);
            if (what == "result") return counters;
            if (what != "run") return "sarcmummynpc <run|result>";
            bool on = ShortcutOn("sarcmummynpc");
            bool eaBug = !SmRemove(true, true, false, false);
            bool fixedPure = SmRemove(true, true, false, on);
            bool controls = eaBug && !SmRemove(false, true, false, true) && !SmRemove(true, false, false, true) && !SmRemove(true, true, true, true);
            return B582Line("sarcmummynpc", on, "a reset or deleted sarcophagus takes its mummy out of NpcHousehold", fixedPure && sSmMissing.Length == 0,
                "EA leaves it (" + eaBug + "), no mummy / not in NpcHousehold / world going kept", controls, sSmMissing, counters);
        }
    }
}
