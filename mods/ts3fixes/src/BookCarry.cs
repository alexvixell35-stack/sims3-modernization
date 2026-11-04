using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // bookcarry (docs/515): GetBook picks the book up with a carry state machine (Sim.CarryStateMachine). When a Sim who
    // reads standing or sitting puts the book in the inventory, Book.StopUsingBook sets Sim.CarryStateMachine = null
    // without asking that machine to exit; EA's own carry exits (CarrySystem.ExitAndKeepHolding, CarryUtils.ExitCarry)
    // request "ExitNoPut" or "Exit" first. The machine keeps its engine driver until the garbage collector finalizes the
    // script client. (Not the cause of the stuck-arm pose a player reported: tested live, docs/515 §1.)
    // Fix, switch on: after EA's method, a dropped machine is ended as ExitAndKeepHolding does (RequestState "x"
    // "ExitNoPut"), then disposed, which releases its driver. [2043] machines dropped (also with the switch off), [2044]
    // ended.
    public static partial class Fixes
    {
        public class BookCarryHook
        {
            public bool StopHook(Sim Actor, StateMachineClient smcRead, bool bPutInInventory)
            {
                StateMachineClient before = null;
                try { before = Actor != null ? Actor.CarryStateMachine : null; } catch (Exception) { }
                bool r = StopOrig(Actor, smcRead, bPutInInventory);
                try
                {
                    if (BookCarryCore.Dropped(before, smcRead, Actor.CarryStateMachine))
                    {
                        PatchCounters()[2043]++;
                        bool armed = sBcSim != null && Actor == sBcSim; // the agent check's Sim; kept only while armed
                        if (armed) { sBcLast = before; sBcSeen++; }
                        if (ShortcutOn("bookcarry") && BcEnd(before) && armed) sBcEnded++;
                    }
                }
                catch (Exception) { } // the check must never change what EA's method returns
                return r;
            }
            public bool StopOrig(Sim Actor, StateMachineClient smcRead, bool bPutInInventory) { return false; } // receives Book.StopUsingBook's original header
        }

        static bool BcEnd(StateMachineClient m)
        {
            // StopUsingBook runs in the reading interaction, where RequestState may wait for the state; anywhere else the
            // waiting form throws NotSupportedException, so the request is made without waiting
            try { m.RequestState("x", "ExitNoPut"); }
            catch (NotSupportedException) { try { m.RequestState(false, "x", "ExitNoPut"); } catch (Exception) { } }
            catch (Exception) { }
            try { m.Dispose(); PatchCounters()[2044]++; return true; } catch (Exception) { return false; }
        }

        static Sim sBcSim;
        static StateMachineClient sBcLast;
        static int sBcSeen, sBcEnded;

        // What "state" returns (reflect_call describes its fields, so a repro "until" can poll them).
        public class BcState { public bool reading, carry, ready; public int dropped; }

        // The machine's engine driver handle (StateMachineClient.mDriver.mHandle): nonzero while the engine still runs the
        // machine, 0 once Dispose released it. IsValid is no test of that: it asks whether the event queue is valid.
        static long BcHandle(StateMachineClient m)
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            object d = typeof(StateMachineClient).GetField("mDriver", inst).GetValue(m);
            return Convert.ToInt64(d.GetType().GetField("mHandle", inst).GetValue(d));
        }

        static Sim BcSim(string name)
        {
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>()) if (s.FullName == name) return s;
            throw new InvalidOperationException("bookcarry: no Sim " + name);
        }

        // Agent checks for docs/515 (repro book-carry-check), on one Sim (args "<check> <full name>"):
        //   "cure":  cancels the Sim's interactions and restarts the Sim's idles (AnimationUtil.StopAllAnimation, then the default
        //           pose), which ends an arm pose already stuck (a save made while stuck loads stuck), so the arm after it
        //           starts from a normal pose and the player can see the outcome. Puts the Sim in the everyday outfit (a
        //           shower cancelled here leaves a towel, and EA's read does not start in it).
        //   "read":  queues EA's ReadBook at high priority (from the Sim's inventory) on a book in it,
        //           so the book goes back to the inventory at the end. Arms the check. Throws when the Sim has no book.
        //   "state": the Sim's read so far (BcState): reading, carry set, dropped since "read"; ready = holding the book
        //           while reading, or a read already ended with a drop. The repro polls it before "stop".
        //   "stop":  cancels the read, so EA's DoReadBook ends through StopUsingBook; a read that already ended that way
        //           passes, anything else throws.
        //   "expect left" (switch off) / "expect ended" (switch on): throws unless a read since "read" dropped a carry
        //           machine of this Sim (seen +1 or more). Off: ended +0 and the last dropped machine still holds its engine driver.
        //           On: every dropped machine ended and the last one's driver released. Disarms the check.
        public static object BookCarryCheck(string arg)
        {
            string what = null;
            foreach (string w in new string[] { "cure", "read", "state", "stop", "expect left", "expect ended" }) if (arg.StartsWith(w + " ")) what = w;
            if (what == null) return "bookcarry <cure|read|state|stop|expect left|expect ended> <full name>";
            string name = arg.Substring(what.Length + 1);
            Sim sim = BcSim(name);
            if (what == "cure")
            {
                sim.InteractionQueue.CancelAllInteractions();
                bool stopped = AnimationUtil.StopAllAnimation(sim);
                sim.StartDefaultPose();
                string outfit = "everyday";
                try { sim.SwitchToOutfitWithoutSpin(Sims3.SimIFace.CAS.OutfitCategories.Everyday); }
                catch (Exception ex) { outfit = ex.GetType().Name; }
                return name + ": idles restarted (StopAllAnimation " + stopped + "), outfit " + outfit;
            }
            if (what == "read")
            {
                Book book = null;
                foreach (Book b in sim.Inventory.FindAll<Book>(false)) { book = b; break; }
                if (book == null) throw new InvalidOperationException("bookcarry read: " + name + " has no book in the inventory");
                InteractionInstance ii = ReadBook.FromSimInventorySingleton.CreateInstance(book, sim, new InteractionPriority(InteractionPriorityLevel.High), false, true);
                bool queued = sim.InteractionQueue.Add(ii);
                sBcSim = sim; sBcLast = null; sBcSeen = 0; sBcEnded = 0;
                return name + ": ReadBook queued " + queued + " on \"" + book.Title + "\"";
            }
            if (what == "state")
            {
                BcState bs = new BcState();
                bs.reading = sim.CurrentInteraction is ReadBook;
                bs.carry = sim.CarryStateMachine != null;
                bs.dropped = sBcSim == sim ? sBcSeen : 0;
                bs.ready = (bs.reading && bs.carry) || bs.dropped > 0;
                return bs;
            }
            if (what == "stop")
            {
                InteractionInstance cur = sim.CurrentInteraction;
                if (!(cur is ReadBook))
                {
                    if (sBcSim == sim && sBcSeen > 0) return name + ": the read already ended (dropped +" + sBcSeen + ")";
                    throw new InvalidOperationException("bookcarry stop: " + name + " is not reading and no read ended (" + (cur != null ? cur.GetType().Name : "nothing") + ")");
                }
                bool carry = sim.CarryStateMachine != null;
                sim.InteractionQueue.CancelAllInteractions();
                return name + ": reading with carry " + carry + "; read cancelled";
            }
            bool on = ShortcutOn("bookcarry");
            if (sBcSim != sim) throw new InvalidOperationException("bookcarry " + what + ": no read armed on " + name);
            int seen = sBcSeen, ended = sBcEnded;
            StateMachineClient m = sBcLast;
            sBcSim = null; sBcLast = null;
            bool valid = false; try { valid = m != null && m.IsValid; } catch (Exception) { }
            long h = m != null ? BcHandle(m) : -1;
            bool ok = seen >= 1 && m != null && (what == "expect left" ? !on && ended == 0 && h != 0 : on && ended == seen && h == 0);
            string st = name + ": seen +" + seen + ", ended +" + ended + ", dropped machine " + (m == null ? "none" : "driver " + (h != 0 ? "held" : "released") + ", event queue valid " + valid);
            if (!ok) throw new InvalidOperationException("bookcarry " + what + ": wrong outcome (switch " + on + "); " + st);
            return "as expected; " + st;
        }
    }
}
