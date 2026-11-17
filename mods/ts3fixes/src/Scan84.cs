using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Counters;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Scan triage 1117 real hits (docs/614): five EA bugs where an object, a surface or a Sim is left in a state EA's own
    // code next to it never leaves it in. All default off; each built from the EA code beside the bug.
    public static partial class Fixes
    {
        // ---- docs/614 §1 barputdownwait -----------------------------------------------------------------------------------
        // [verified, ilspycmd BarAdvanced+PutDownCarriedObjectHelper] Wait() is `while (!mHasExecuted) Sleep(0)`; only
        // HandlerInternal sets mHasExecuted, and it runs only from Handler (anim events 100/101 during the "Exit - Put Down
        // ..." request), which queues it as a OneShotFunction. [verified, BarAdvanced::PutDownCarriedObject] Wait is called
        // after the requests and stateMachineClient.Dispose(), so no event can fire later: mHandler still null at that point
        // means the bartender's Run never returns. Fix as EA wrote it (CarrySystem.FinishPutDown: when the anim event did not
        // fire, do the put-down step inline): with mHandler null and mHasExecuted false at Wait, run HandlerInternal's steps
        // (ParentToSlot to the destination; on success ExitCarry and leave the object's use list) and return. A queued handler
        // (mHandler set) keeps EA's wait: it runs on the next tick. Household Sims get the fix too: EA clearly intends the
        // put-down to finish, and the vanilla alternative is a Sim frozen forever.
        // Counters: [3461] put-downs finished inline, [3462] hangs entered with the switch off, [3463] errors.
        static readonly Type sBpwType = typeof(BarAdvanced).GetNestedType("PutDownCarriedObjectHelper", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sBpwDone = sBpwType == null ? null : sBpwType.GetField("mHasExecuted", kInstAll);
        static readonly FieldInfo sBpwHandler = sBpwType == null ? null : sBpwType.GetField("mHandler", kInstAll);
        static readonly FieldInfo sBpwSuccess = sBpwType == null ? null : sBpwType.GetField("mSuccess", kInstAll);
        static readonly FieldInfo sBpwActor = sBpwType == null ? null : sBpwType.GetField("mActor", kInstAll);
        static readonly FieldInfo sBpwObject = sBpwType == null ? null : sBpwType.GetField("mObject", kInstAll);
        static readonly FieldInfo sBpwDest = sBpwType == null ? null : sBpwType.GetField("mDestination", kInstAll);
        static string sBpwMissing = "";

        static void ApplyBarPutDownWait()
        {
            sBpwMissing = sBpwType == null ? "BarAdvanced.PutDownCarriedObjectHelper"
                : (sBpwDone == null || sBpwHandler == null || sBpwSuccess == null || sBpwActor == null || sBpwObject == null || sBpwDest == null) ? "PutDownCarriedObjectHelper fields" : "";
            if (sBpwMissing.Length > 0) { AppliedLog().Add("barputdownwait: not found: " + sBpwMissing); return; }
            Patch(sBpwType, "Wait", Type.EmptyTypes, typeof(BpwWaitHook), "WaitHook", "WaitOrig");
        }

        // Pure: finish the put-down inline now? (EA: never; it waits for a handler that can no longer come.)
        internal static bool BpwInline(bool on, bool executed, bool handlerQueued)
        {
            return on && !executed && !handlerQueued;
        }

        // HandlerInternal's steps without the OneShotFunction (there is none).
        static void BpwFinish(object helper)
        {
            Sim actor = sBpwActor.GetValue(helper) as Sim;
            IGameObject obj = sBpwObject.GetValue(helper) as IGameObject;
            BarAdvanced.BarDestination dest = (BarAdvanced.BarDestination)sBpwDest.GetValue(helper);
            bool ok = false;
            if (obj != null && dest.Object != null) ok = obj.ParentToSlot(dest.Object as IGameObject, dest.PlacementSlot);
            if (ok && actor != null)
            {
                CarrySystem.ExitCarry(actor);
                if (obj.IsActorUsingMe(actor)) obj.RemoveFromUseList(actor);
            }
            sBpwSuccess.SetValue(helper, ok);
            sBpwDone.SetValue(helper, true);
        }

        public class BpwWaitHook
        {
            public void WaitHook()
            {
                object self = this;
                try
                {
                    if (sBpwMissing.Length == 0)
                    {
                        bool done = (bool)sBpwDone.GetValue(self), queued = sBpwHandler.GetValue(self) != null;
                        bool on = ShortcutOn("barputdownwait");
                        if (BpwInline(on, done, queued)) { BpwFinish(self); PatchCounters()[3461]++; return; }
                        if (!on && !done && !queued) PatchCounters()[3462]++;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3463]++; }
                WaitOrig();
            }
            public void WaitOrig() { } // receives BarAdvanced+PutDownCarriedObjectHelper.Wait
        }

        public static object BarPutDownWaitCheck(string what)
        {
            string counters = B582Counters("barputdownwait", 3461, new[] { "finished inline", "hangs entered, switch off", "errors" }, sBpwMissing);
            if (what == "result") return counters;
            if (what != "run") return "barputdownwait <run|result>";
            bool on = ShortcutOn("barputdownwait");
            bool eaBug = !BpwInline(false, false, false);
            bool fixedPure = on && BpwInline(on, false, false);
            bool controls = eaBug && !BpwInline(true, true, false) && !BpwInline(true, false, true);
            // live: a helper object whose handler never fired, through the swapped Wait (on returns; off would hang, so the
            // off arm only reads the decision)
            string live = "off: live replay skipped (EA's Wait would never return)";
            bool liveOk = true;
            if (on && sBpwMissing.Length == 0)
            {
                try
                {
                    object h = Activator.CreateInstance(sBpwType, true);   // no actor, no object: ParentToSlot is not called
                    MethodInfo w = sBpwType.GetMethod("Wait", kInstAll);
                    int[] c = PatchCounters(); int c0 = c[3461];
                    ((BpwWaitFn)Delegate.CreateDelegate(typeof(BpwWaitFn), h, w))();
                    c[3461] = c0;
                    liveOk = (bool)sBpwDone.GetValue(h) && !(bool)sBpwSuccess.GetValue(h);
                    live = "live replay: Wait returned with the handler never fired, success=" + sBpwSuccess.GetValue(h);
                }
                catch (Exception e) { liveOk = false; live = "live replay threw " + (e.InnerException ?? e).GetType().Name; }
            }
            return B582Line("barputdownwait", on, "a put-down whose anim event never fired finishes", fixedPure && liveOk && sBpwMissing.Length == 0,
                "EA waits forever (" + eaBug + "), done / queued handler keep EA's path, " + live, controls && liveOk, sBpwMissing, counters);
        }

        delegate void BpwWaitFn();

        // ---- docs/614 §2 putdownsurfaceuse --------------------------------------------------------------------------------
        // [verified, ilspycmd PutDown::TryToPlaceOnSurface] after surface.AddToUseList(Actor) and Actor.Inventory.SetInUse(Target)
        // and the yielding swipe animation, a failed ParentToSlot (the slot filled meanwhile) returns false with both still
        // held; Cleanup only destroys the jig. CarrySystem.PutDownOnSurface and this method refuse a surface that is InUse,
        // so that counter or table takes no more put-downs. The success path and CarrySystem.FinishPutDown release the
        // surface. Fix as EA intended: on that false return, release what this call took (the surface's use by the Actor and
        // the item's in-use flag), only when this call took it (both were free before). Household Sims get the fix: EA
        // releases both on its own success path, the leak has no intended effect.
        // Counters: [3464] releases, [3465] leaks with the switch off, [3466] errors.
        static readonly MethodInfo sPduFindItem = typeof(Inventory).GetMethod("FindItem", kInstAll, null, new Type[] { typeof(IGameObject) }, null);
        static string sPduMissing = "";

        static void ApplyPutDownSurfaceUse()
        {
            sPduMissing = sPduFindItem == null ? "Inventory.FindItem(IGameObject)" : "";
            if (sPduMissing.Length > 0) { AppliedLog().Add("putdownsurfaceuse: not found: " + sPduMissing); return; }
            Patch(typeof(PutDown), "TryToPlaceOnSurface", new Type[] { typeof(bool) }, typeof(PduSurfaceHook), "SurfaceHook", "SurfaceOrig");
        }

        // Pure: release after this call? usedBefore / inUseBefore = held before EA's body ran.
        internal static bool PduReleaseSurface(bool on, bool result, bool usedBefore, bool usedAfter)
        {
            return on && !result && !usedBefore && usedAfter;
        }

        static bool PduItemInUse(Inventory inv, IGameObject obj)
        {
            if (inv == null || obj == null) return false;
            InventoryItem it = sPduFindItem.Invoke(inv, new object[] { obj }) as InventoryItem;   // Inventory is not swapped
            return it != null && it.InUse;
        }

        public class PduSurfaceHook
        {
            public bool SurfaceHook(bool sameRoomOnly)
            {
                PutDown self = (object)this as PutDown;
                ISurface surface = null;
                bool on = false, usedBefore = false, inUseBefore = false;
                try
                {
                    if (self != null && sPduMissing.Length == 0)
                    {
                        on = ShortcutOn("putdownsurfaceuse");
                        // EA's first call, same tick, no yield before it: the same surface EA's body picks
                        surface = GlobalFunctions.FindNearestSurfaceOfType(self.Actor, SurfaceType.Normal, self.Target);
                        usedBefore = surface != null && surface.IsActorUsingMe(self.Actor);
                        inUseBefore = PduItemInUse(self.Actor.Inventory, self.Target);
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3466]++; surface = null; }
                bool r = SurfaceOrig(sameRoomOnly);
                try
                {
                    if (surface != null && !r)
                    {
                        bool usedAfter = surface.IsActorUsingMe(self.Actor);
                        if (PduReleaseSurface(on, r, usedBefore, usedAfter))
                        {
                            surface.RemoveFromUseList(self.Actor);
                            if (!inUseBefore && PduItemInUse(self.Actor.Inventory, self.Target)) self.Actor.Inventory.SetNotInUse(self.Target);
                            PatchCounters()[3464]++;
                        }
                        else if (!on && !usedBefore && usedAfter) PatchCounters()[3465]++;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3466]++; }
                return r;
            }
            public bool SurfaceOrig(bool sameRoomOnly) { return false; } // receives PutDown.TryToPlaceOnSurface
        }

        public static object PutDownSurfaceUseCheck(string what)
        {
            string counters = B582Counters("putdownsurfaceuse", 3464, new[] { "releases", "leaks, switch off", "errors" }, sPduMissing);
            if (what == "result") return counters;
            if (what != "run") return "putdownsurfaceuse <run|result>";
            bool on = ShortcutOn("putdownsurfaceuse");
            bool eaBug = !PduReleaseSurface(false, false, false, true);
            bool fixedPure = on && PduReleaseSurface(on, false, false, true);
            bool controls = eaBug && !PduReleaseSurface(true, true, false, true) && !PduReleaseSurface(true, false, true, true) && !PduReleaseSurface(true, false, false, false);
            return B582Line("putdownsurfaceuse", on, "a failed surface put-down releases the surface", fixedPure && sPduMissing.Length == 0,
                "EA keeps it in use (" + eaBug + "), success / held before / not held kept", controls, sPduMissing, counters);
        }

        // ---- docs/614 §3 swiperestore -------------------------------------------------------------------------------------
        // [verified, ilspycmd TraitFunctions+SwipeSomething::Run] the object is faded out, taken off the victim's use list and
        // RemoveFromWorld()'d, then `if (!SharedFamilyInventory.Inventory.TryToAdd(mFinalStolenObject)) return false`. On that
        // path no ReplenishObject alarm is set (the success path's restore of an off-lot original) and the clone (off the
        // player's lot) is in no inventory: the victim's object is out of the world for good and the clone leaks. Fix as EA
        // intended (the steal did not happen): put the original back where it was (AddToWorld, its saved parent slot, opacity
        // 1) and destroy the clone when it is not the original. Only when Run returned false with the original out of the
        // world, not destroyed, and the stolen object in no inventory. All households: the victim's object comes back.
        // Counters: [3467] restores, [3468] lost with the switch off, [3469] errors.
        static readonly Type sSwrType = typeof(TraitFunctions).GetNestedType("SwipeSomething", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sSwrObj = sSwrType == null ? null : sSwrType.GetField("mObjectToSteal", kInstAll);
        static readonly FieldInfo sSwrFinal = sSwrType == null ? null : sSwrType.GetField("mFinalStolenObject", kInstAll);
        static readonly FieldInfo sSwrParent = sSwrType == null ? null : sSwrType.GetField("mObjectParent", kInstAll);
        static readonly FieldInfo sSwrSlot = sSwrType == null ? null : sSwrType.GetField("mContainedSlot", kInstAll);
        static string sSwrMissing = "";

        static void ApplySwipeRestore()
        {
            sSwrMissing = sSwrType == null ? "TraitFunctions.SwipeSomething"
                : (sSwrObj == null || sSwrFinal == null || sSwrParent == null || sSwrSlot == null) ? "SwipeSomething fields" : "";
            if (sSwrMissing.Length > 0) { AppliedLog().Add("swiperestore: not found: " + sSwrMissing); return; }
            Patch(sSwrType, "Run", Type.EmptyTypes, typeof(SwrRunHook), "RunHook", "RunOrig");
        }

        // Pure: restore the victim's object after Run?
        internal static bool SwrRestore(bool on, bool result, bool origOutOfWorld, bool origAlive, bool stolenInInventory)
        {
            return on && !result && origOutOfWorld && origAlive && !stolenInInventory;
        }

        public class SwrRunHook
        {
            public bool RunHook()
            {
                object self = this;
                bool r = RunOrig();
                try
                {
                    if (r || sSwrMissing.Length > 0) return r;
                    GameObject orig = sSwrObj.GetValue(self) as GameObject, fin = sSwrFinal.GetValue(self) as GameObject;
                    if (orig == null) return r;
                    bool alive = !orig.HasBeenDestroyed, outW = !orig.InWorld, inInv = fin != null && fin.InInventory;
                    bool on = ShortcutOn("swiperestore");
                    if (!SwrRestore(on, r, outW, alive, inInv)) { if (!on && SwrRestore(true, r, outW, alive, inInv)) PatchCounters()[3468]++; return r; }
                    orig.AddToWorld();
                    GameObject parent = sSwrParent.GetValue(self) as GameObject;
                    Slot slot = (Slot)sSwrSlot.GetValue(self);
                    if (parent != null && !parent.HasBeenDestroyed && slot != Slot.ContainmentSlot_Sentinel) orig.ParentToSlot(parent, slot);
                    orig.SetOpacity(1f, 0f);
                    if (fin != null && !ReferenceEquals(fin, orig) && !fin.HasBeenDestroyed) fin.Destroy();
                    PatchCounters()[3467]++;
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3469]++; }
                return r;
            }
            public bool RunOrig() { return false; } // receives TraitFunctions+SwipeSomething.Run
        }

        public static object SwipeRestoreCheck(string what)
        {
            string counters = B582Counters("swiperestore", 3467, new[] { "restores", "lost, switch off", "errors" }, sSwrMissing);
            if (what == "result") return counters;
            if (what != "run") return "swiperestore <run|result>";
            bool on = ShortcutOn("swiperestore");
            bool eaBug = !SwrRestore(false, false, true, true, false);
            bool fixedPure = on && SwrRestore(on, false, true, true, false);
            bool controls = eaBug && !SwrRestore(true, true, true, true, false) && !SwrRestore(true, false, false, true, false)
                && !SwrRestore(true, false, true, false, false) && !SwrRestore(true, false, true, true, true);
            return B582Line("swiperestore", on, "a failed swipe puts the victim's object back", fixedPure && sSwrMissing.Length == 0,
                "EA loses it (" + eaBug + "), success / in world / destroyed / stolen in inventory kept", controls, sSwrMissing, counters);
        }

        // ---- docs/614 §4 unicornspawnfail ---------------------------------------------------------------------------------
        // [verified, ilspycmd UnicornPool::SpawnNPCUnicorn] the NPC unicorn is instantiated off screen at opacity 0 and gets
        // OccultUnicorn+UnicornSpawn at High. [verified, UnicornSpawn::Run] FadeOut first, then `return false` when
        // FindGoodLocation finds no spot from the lot corner: no Cleanup override, so an invisible unicorn Sim stays in the
        // world (its only `return false`; every other path returns true). Fix as EA ends an NPC unicorn
        // (OccultUnicorn+UnicornDespawn: FadeOut(destroyObject: true)): on that false return the unicorn object is destroyed
        // and its description stays in the pool for the next spawn. Household rule: a player Sim (IsPlayerSim) keeps EA's path.
        // Counters: [3470] failed spawns ended, [3471] left invisible with the switch off, [3472] errors.
        static string sUsfMissing = "";

        static void ApplyUnicornSpawnFail()
        {
            Type t = typeof(OccultUnicorn).GetNestedType("UnicornSpawn", BindingFlags.Public | BindingFlags.NonPublic);
            sUsfMissing = t == null ? "OccultUnicorn.UnicornSpawn" : "";
            if (sUsfMissing.Length > 0) { AppliedLog().Add("unicornspawnfail: not found: " + sUsfMissing); return; }
            Patch(t, "Run", Type.EmptyTypes, typeof(UsfRunHook), "RunHook", "RunOrig");
        }

        internal static bool UsfEnd(bool on, bool result, bool player, bool alive)
        {
            return on && !result && !player && alive;
        }

        public class UsfRunHook
        {
            public bool RunHook()
            {
                bool r = RunOrig();
                try
                {
                    if (r) return r;
                    InteractionInstance self = (object)this as InteractionInstance;
                    Sim a = self == null ? null : self.InstanceActor;
                    if (a == null) return r;
                    bool on = ShortcutOn("unicornspawnfail"), player = IsPlayerSim(a), alive = !a.HasBeenDestroyed;
                    if (UsfEnd(on, r, player, alive)) { a.FadeOut(false, true, 0.15f); PatchCounters()[3470]++; }   // EA UnicornDespawn: 0.15 min fade, destroy on its alarm
                    else if (!on && UsfEnd(true, r, player, alive)) PatchCounters()[3471]++;
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3472]++; }
                return r;
            }
            public bool RunOrig() { return false; } // receives OccultUnicorn+UnicornSpawn.Run
        }

        public static object UnicornSpawnFailCheck(string what)
        {
            string counters = B582Counters("unicornspawnfail", 3470, new[] { "failed spawns ended", "left invisible, switch off", "errors" }, sUsfMissing);
            if (what == "result") return counters;
            if (what != "run") return "unicornspawnfail <run|result>";
            bool on = ShortcutOn("unicornspawnfail");
            bool eaBug = !UsfEnd(false, false, false, true);
            bool fixedPure = on && UsfEnd(on, false, false, true);
            bool controls = eaBug && !UsfEnd(true, true, false, true) && !UsfEnd(true, false, true, true) && !UsfEnd(true, false, false, false);
            return B582Line("unicornspawnfail", on, "a unicorn spawn with no spot ends like EA's despawn", fixedPure && sUsfMissing.Length == 0,
                "EA leaves it invisible (" + eaBug + "), spawned / player Sim / destroyed kept", controls, sUsfMissing, counters);
        }

        // ---- docs/614 §5 datadiscputback ----------------------------------------------------------------------------------
        // [verified, ilspycmd DataDiscStorage+AddAllDataDiscs::Run and +DumpAllDataDiscs::Run] each disc is TryToRemove'd from
        // one inventory, then `if (!other.TryToAdd(disc)) return false`: that disc is in neither inventory (lost) and the rest
        // are not moved. Fix as EA intended (a move, never a loss): a refused disc goes back to the inventory it came from
        // (TryToAdd; EA's own remove just emptied its spot) and the loop goes on with the next disc; Run returns false when any
        // disc stayed behind, as EA's failure does. No yield in either Run. All households (the player's disc is the one lost).
        // Counters: [3473] discs put back, [3474] discs lost because the put-back was refused too, [3475] errors.
        static string sDdpMissing = "";

        static void ApplyDataDiscPutBack()
        {
            Type add = typeof(DataDiscStorage).GetNestedType("AddAllDataDiscs", BindingFlags.Public | BindingFlags.NonPublic);
            Type dump = typeof(DataDiscStorage).GetNestedType("DumpAllDataDiscs", BindingFlags.Public | BindingFlags.NonPublic);
            sDdpMissing = add == null || dump == null ? "DataDiscStorage.AddAllDataDiscs/DumpAllDataDiscs" : "";
            if (sDdpMissing.Length > 0) { AppliedLog().Add("datadiscputback: not found: " + sDdpMissing); return; }
            Patch(add, "Run", Type.EmptyTypes, typeof(DdpAddHook), "RunHook", "RunOrig");
            Patch(dump, "Run", Type.EmptyTypes, typeof(DdpDumpHook), "RunHook", "RunOrig");
        }

        // The fixed move (pure over delegates so the Check can run it on lists). Returns true when every item moved.
        internal static bool DdpMove<T>(List<T> items, Predicate<T> remove, Predicate<T> add, Predicate<T> putBack, ref int putBacks, ref int lost)
        {
            bool all = true;
            foreach (T it in items)
            {
                if (!remove(it)) return false;   // EA: a disc it cannot take out ends the move
                if (add(it)) continue;
                all = false;
                if (putBack(it)) putBacks++; else lost++;
            }
            return all;
        }

        static bool DdpRun(Inventory from, Inventory to)
        {
            if (from == null || to == null) return false;
            List<DataDisc> list = from.FindAll<DataDisc>(false);
            int back = 0, lost = 0;
            bool r = DdpMove(list, delegate(DataDisc d) { return from.TryToRemove(d); }, delegate(DataDisc d) { return to.TryToAdd(d); },
                delegate(DataDisc d) { return from.TryToAdd(d); }, ref back, ref lost);
            PatchCounters()[3473] += back;
            PatchCounters()[3474] += lost;
            return r;
        }

        static bool DdpHook(object self, bool addAll, out bool handled)
        {
            handled = false;
            try
            {
                if (sDdpMissing.Length > 0 || !ShortcutOn("datadiscputback")) return false;
                InteractionInstance ii = self as InteractionInstance;
                Sim a = ii == null ? null : ii.InstanceActor;
                DataDiscStorage t = ii == null ? null : ii.Target as DataDiscStorage;
                if (a == null || t == null) return false;
                handled = true;
                return addAll ? DdpRun(a.Inventory, t.Inventory) : DdpRun(t.Inventory, a.Inventory);
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3475]++; return false; }   // handled stays as set: no second move by EA's body
        }

        public class DdpAddHook
        {
            public bool RunHook() { bool h; bool r = DdpHook(this, true, out h); return h ? r : RunOrig(); }
            public bool RunOrig() { return false; } // receives DataDiscStorage+AddAllDataDiscs.Run
        }

        public class DdpDumpHook
        {
            public bool RunHook() { bool h; bool r = DdpHook(this, false, out h); return h ? r : RunOrig(); }
            public bool RunOrig() { return false; } // receives DataDiscStorage+DumpAllDataDiscs.Run
        }

        // DataDiscPutBackCheck run: the fixed move on plain lists (one disc refused) vs EA's loop: EA loses it and stops,
        // ours puts it back and moves the rest. Controls: all accepted (same as EA), a disc that cannot be removed (EA's stop).
        public static object DataDiscPutBackCheck(string what)
        {
            string counters = B582Counters("datadiscputback", 3473, new[] { "discs put back", "discs lost (put-back refused)", "errors" }, sDdpMissing);
            if (what == "result") return counters;
            if (what != "run") return "datadiscputback <run|result>";
            bool on = ShortcutOn("datadiscputback");
            List<int> src = new List<int>(new int[] { 1, 2, 3 }), dst = new List<int>();
            int back = 0, lost = 0;
            bool r;
            if (on)
                r = DdpMove(new List<int>(src), delegate(int d) { return src.Remove(d); }, delegate(int d) { if (d == 2) return false; dst.Add(d); return true; },
                    delegate(int d) { src.Add(d); return true; }, ref back, ref lost);
            else
            {   // EA's loop
                r = true;
                foreach (int d in new List<int>(src)) { if (!src.Remove(d)) { r = false; break; } if (d == 2) { r = false; lost++; break; } dst.Add(d); }
            }
            bool fixedNow = on && !r && back == 1 && lost == 0 && src.Count == 1 && src[0] == 2 && dst.Count == 2;
            List<int> s2 = new List<int>(new int[] { 1, 2 }), d2 = new List<int>();
            int b2 = 0, l2 = 0;
            bool allOk = DdpMove(new List<int>(s2), delegate(int d) { return s2.Remove(d); }, delegate(int d) { d2.Add(d); return true; }, delegate(int d) { return true; }, ref b2, ref l2);
            int b3 = 0, l3 = 0;
            bool stop = !DdpMove(new List<int>(new int[] { 1 }), delegate(int d) { return false; }, delegate(int d) { return true; }, delegate(int d) { return true; }, ref b3, ref l3);
            bool controls = allOk && d2.Count == 2 && b2 == 0 && stop && b3 == 0 && l3 == 0;
            return B582Line("datadiscputback", on, "a refused disc goes back and the rest move", fixedNow && sDdpMissing.Length == 0,
                "EA: lost " + (on ? "n/a" : lost.ToString()) + " moved " + dst.Count + "; all accepted same as EA=" + allOk + ", unremovable disc stops=" + stop, controls, sDdpMissing, counters);
        }
    
        static readonly int sLive_barputdownwait = LiveAdd("barputdownwait", () => EpLive(typeof(BpwWaitHook), 1, "sBpwDone", sBpwDone, typeof(bool), "sBpwHandler", sBpwHandler, null));
        static readonly int sLive_putdownsurfaceuse = LiveAdd("putdownsurfaceuse", () => EpLive(typeof(PduSurfaceHook), 1));
        static readonly int sLive_swiperestore = LiveAdd("swiperestore", () => EpLive(typeof(SwrRunHook), 1, "sSwrObj", sSwrObj, null, "sSwrFinal", sSwrFinal, null, "sSwrParent", sSwrParent, null, "sSwrSlot", sSwrSlot, null));
        static readonly int sLive_unicornspawnfail = LiveAdd("unicornspawnfail", () => EpLive(typeof(UsfRunHook), 1));
        static readonly int sLive_datadiscputback = LiveAdd("datadiscputback", () => EpJoin(EpLive(typeof(DdpAddHook), 1), EpLive(typeof(DdpDumpHook), 1)));
    }
}
