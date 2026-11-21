using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // bookhand (docs/251): Book.StopUsingBook (standing or sitting read, book from the inventory) adds EA's own
    // PutBookInInventoryHandler as a one-shot event 100 handler on the read machine, sets Sim.CarryStateMachine = null at
    // once and requests "Exit". When that clip never reaches event 100, the book stays in the hand with no carry machine, and
    // every later EA put-down throws: CarrySystem.PutDown reads the null machine (NullReferenceException in PutDownOnSurface or
    // PutDownOnSpot) and InteractionQueue.PutDownCarriedObjects throws "left an object in the Sim's hand with no carry state
    // machine". Fix, switch on: at those two calls, only in that state and only where EA would throw, EA's own
    // PutBookInInventoryHandler runs first (inventory add, unparent, bind pose reset), so the book ends where EA's code had
    // already sent it and the put-down finds an empty hand. Applies to every Sim, household too: it replaces an exception with
    // the outcome EA chose for that book (no cap, skip, teleport or fake). Other objects in that state are left to EA.
    // [876] stuck books seen at a put-down that would throw (also with the switch off), [877] of those moved out of the hand.
    // Logging (bookhand or bookhanddiag on): 10 s after StopUsingBook left the book in the hand with no carry machine, one
    // "bookhand: still in hand" line (Sim, household, interactions, posture, what was queued at the stop).
    public static partial class Fixes
    {
        public static class BookHandPutDownHook
        {
            public static void PutDownHook(Sim sim, SurfaceType surfaceSlotType, bool forceRoute)
            {
                try { if (sim != null) BhAt(sim, BookHandCore.PutDownWouldThrow(BhStuck(sim), sim.LotCurrent == null), "CarrySystem.PutDown"); }
                catch (Exception) { } // the guard must never change what EA's method does
                PutDownOrig(sim, surfaceSlotType, forceRoute);
            }
            public static void PutDownOrig(Sim sim, SurfaceType surfaceSlotType, bool forceRoute) { } // receives CarrySystem.PutDown(Sim, SurfaceType, bool)'s original header
        }

        public class BookHandQueueHook
        {
            public void PdcoHook(InteractionInstance i)
            {
                try
                {
                    // every interaction end passes here: the cheap stuck test on the interaction's actor first, the queue's own
                    // mActor (reflection) only then
                    Sim a = i != null ? i.InstanceActor as Sim : null;
                    InteractionQueue q = (InteractionQueue)(object)this;
                    Sim sim = a != null && BhStuck(a) ? sBhActor.GetValue(q) as Sim : null;
                    if (sim != null)
                    {
                        bool same = false;
                        foreach (InteractionInstance head in q.InteractionList) { same = head.GroupId == i.GroupId; break; }
                        bool carrying = sim.Posture != null && sim.Posture.Satisfies(CommodityKind.CarryingObject, null);
                        BhAt(sim, BookHandCore.QueueWouldThrow(BhStuck(sim), same, carrying), "InteractionQueue.PutDownCarriedObjects after " + i.GetType().Name);
                    }
                }
                catch (Exception) { }
                PdcoOrig(i);
            }
            public void PdcoOrig(InteractionInstance i) { } // receives InteractionQueue.PutDownCarriedObjects's original header
        }

        static readonly FieldInfo sBhActor = typeof(InteractionQueue).GetField("mActor", BindingFlags.Instance | BindingFlags.NonPublic);
        static int sBhLines;

        static Book BhBook(Sim sim) { return sim.GetObjectInRightHand() as Book; }

        static bool BhBusy(Sim sim) { return sim.CurrentInteraction != null || sim.InteractionQueue.TransitionInteraction != null || sim.IsRouting; }

        static bool BhStuck(Sim sim)
        {
            Book b = BhBook(sim);
            return BookHandCore.Stuck(b != null, b != null && b.Parent == sim, sim.CarryStateMachine == null);
        }

        // One put-down site that would throw on a stuck book: count it, and with the switch on run EA's handler first.
        static void BhAt(Sim sim, bool wouldThrow, string where)
        {
            int act = BookHandCore.Action(wouldThrow, ShortcutOn("bookhand"));
            if (act == 0) return;
            int[] c = PatchCounters();
            c[876]++;
            if (sBhCrowd != null && sBhCrowd.ContainsKey(sim)) sBhCrowd[sim] = true; // crowd check: reached the stuck state
            Book b = BhBook(sim);
            if (act == 2) { b.PutBookInInventoryHandler(null, null); if (b.Parent != sim) c[877]++; }
            if (sBhLines++ < BookHandCore.kDiagLines)
                DiagLine("bookhand: " + (act == 2 ? (b.Parent != sim ? "moved to the inventory" : "handler left it in the hand") : "seen (switch off)") + ": " + BhWho(sim) + " at " + where);
        }

        static string BhWho(Sim sim)
        {
            InteractionInstance cur = sim.CurrentInteraction;
            return sim.FullName + ", household " + (sim.Household != null && sim.Household == Household.ActiveHousehold ? "yes" : "no")
                + ", current " + (cur != null ? cur.GetType().Name : "none") + ", posture " + (sim.Posture != null ? sim.Posture.GetType().Name : "none");
        }

        // What the read stopped for, taken before StopUsingBook runs: the read and its cancel reason, the Sim's exit reason,
        // and the first other queued interaction (the likely canceller).
        static string BhStopContext(Sim sim)
        {
            InteractionInstance cur = sim.CurrentInteraction, next = null;
            foreach (InteractionInstance ii in sim.InteractionQueue.InteractionList) if (ii != cur) { next = ii; break; }
            return "read " + (cur != null ? cur.GetType().Name + " cancelled " + cur.CancelledReason : "none") + ", sim exit " + sim.ExitReason
                + ", queued " + (next != null ? next.GetType().Name + (next.Autonomous ? " (autonomous)" : " (user)") : "none");
        }

        // Called by BookCarryHook.StopHook before EA's StopUsingBook. The diag check is armed here, not after the call: EA's
        // RequestState("x", "Exit") waits for the clip, so when the clip never reaches event 100 the call does not return and a
        // check armed after it never ran (no "still in hand" line in any run up to 2026-11-12).
        static void BhStopStart(Sim sim)
        {
            bool armed = sim == sBhSim || (sBhCrowd != null && sBhCrowd.ContainsKey(sim));
            if (armed) sBhStops++;
            if (armed && sBhMode == "animstop")
            {
                // repro stress (labelled): the next tick stops the Sim's clips while the read machine plays Exit
                sBhInterrupts++;
                Simulator.AddObject(new LoopTask(delegate { AnimationUtil.StopAllAnimation(sim); }));
            }
            if (!ShortcutOn("bookhand") && !ShortcutOn("bookhanddiag")) return;
            string ctx = BhStopContext(sim);
            Simulator.AddObject(new LoopTask(delegate
            {
                int t0 = System.Environment.TickCount;
                while (!BookHandCore.DiagDue(unchecked(System.Environment.TickCount - t0))) Simulator.Sleep(10);
                if (sim.HasBeenDestroyed || !BookHandCore.DiagLog(BhStuck(sim), sBhLines)) return;
                sBhLines++;
                DiagLine("bookhand: still in hand " + BookHandCore.kDiagMs / 1000 + " s after StopUsingBook: " + BhWho(sim) + "; at the stop: " + ctx);
            }));
        }

        // Agent checks for docs/251 (repro book-hand-check), on one Sim (args "<check> <full name>"):
        //   "begin":   arms the check on this Sim and takes the counters as the arm's start.
        //   "read":    queues EA's ReadBook (from the inventory) at high priority on a book in the Sim's inventory.
        //   "state":   BhState; ready = reading with the book in hand.
        //   "arm animstop" / "arm none": stress (labelled): the next StopUsingBook of this Sim stops its clips one tick later.
        //   "stop":    cancels the read (the player's cancel), so EA ends it through StopUsingBook.
        //   "probe":   records whether the book is stuck now (book in hand, parented, no carry machine).
        //   "cure":    test cleanup between tries: a stuck book goes through EA's handler, the idles restart.
        //   "expect off": the tally (never throws once a try ran). "expect on": throws when a probe found a stuck book or a
        //              seen put-down was not fixed; N/A when the state never came up. Disarms.
        public class BhState { public bool reading, carry, ready, stuck; public int stops, seen, fixedN; }

        static Sim sBhSim;
        static string sBhMode;
        static int sBhStops, sBhInterrupts, sBhProbes, sBhStuckProbes, sBhCured, sBhSeen0, sBhFixed0;

        public static object BookHandCheck(string arg)
        {
            if (arg.StartsWith("crowd ")) return BhCrowdCheck(arg.Substring(6));
            string what = null;
            foreach (string w in new string[] { "begin", "read", "state", "arm animstop", "arm none", "stop", "probe", "cure", "expect off", "expect on" }) if (arg.StartsWith(w + " ")) what = w;
            if (what == null) return "bookhand <begin|read|state|arm animstop|arm none|stop|probe|cure|expect off|expect on> <full name>";
            Sim sim = BcSim(arg.Substring(what.Length + 1));
            int[] c = PatchCounters();
            if (what == "begin")
            {
                sBhSim = sim; sBhMode = null; sBhStops = sBhInterrupts = sBhProbes = sBhStuckProbes = sBhCured = 0; sBhSeen0 = c[876]; sBhFixed0 = c[877];
                return sim.FullName + ": armed, switch bookhand " + ShortcutOn("bookhand") + ", seen " + sBhSeen0 + ", fixed " + sBhFixed0;
            }
            if (sBhSim != sim) throw new InvalidOperationException("bookhand " + what + ": no check armed on " + sim.FullName + " (begin first); armed: "
                + (sBhSim == null ? "nobody" : sBhSim.FullName + ", destroyed " + sBhSim.HasBeenDestroyed + ", same id " + (sBhSim.ObjectId == sim.ObjectId)));
            if (what == "read") return BhRead(sim);
            if (what == "state")
            {
                BhState s = new BhState();
                s.reading = sim.CurrentInteraction is ReadBook;
                s.carry = sim.CarryStateMachine != null;
                s.ready = s.reading && s.carry && BhBook(sim) != null;
                s.stuck = BhStuck(sim);
                s.stops = sBhStops; s.seen = c[876] - sBhSeen0; s.fixedN = c[877] - sBhFixed0;
                return s;
            }
            if (what == "arm animstop" || what == "arm none") { sBhMode = what == "arm animstop" ? "animstop" : null; return sim.FullName + ": mode " + (sBhMode ?? "none"); }
            if (what == "stop")
            {
                if (!(sim.CurrentInteraction is ReadBook)) throw new InvalidOperationException("bookhand stop: " + sim.FullName + " is not reading (" + (sim.CurrentInteraction != null ? sim.CurrentInteraction.GetType().Name : "nothing") + ")");
                sim.InteractionQueue.CancelAllInteractions();
                return sim.FullName + ": read cancelled";
            }
            if (what == "probe")
            {
                bool stuck = BhStuck(sim);
                sBhProbes++; if (stuck) sBhStuckProbes++;
                sBhMode = null;
                return sim.FullName + ": " + (stuck ? "STUCK: book in hand, no carry machine" : "hand clear or carried normally") + "; " + BhWho(sim);
            }
            if (what == "cure")
            {
                Book b = BhBook(sim);
                bool stuck = BhStuck(sim);
                if (stuck) { b.PutBookInInventoryHandler(null, null); sBhCured++; }
                sim.InteractionQueue.CancelAllInteractions();
                // StopAllAnimation while the cancelled interaction is still routing (an autonomous walk to bed) throws SacsErrorException
                // in the route and resets the Sim (2026-11-08 on arm: a new Sim object, the check lost): restart the idles once it ended.
                // CurrentInteraction is null during the posture transition (EnterSleeping routing to the bed: mIsHeadInteractionActive
                // is false), so also wait for the transition and the route; still busy after 3 s = skip the restart, never stop clips mid-route
                Simulator.AddObject(new LoopTask(delegate
                {
                    int t0 = System.Environment.TickCount;
                    while (!sim.HasBeenDestroyed && BhBusy(sim) && System.Environment.TickCount - t0 < 3000) Simulator.Sleep(10);
                    if (sim.HasBeenDestroyed || BhBusy(sim)) return;
                    AnimationUtil.StopAllAnimation(sim);
                    sim.StartDefaultPose();
                }));
                return sim.FullName + ": " + (stuck ? "stuck book put away (test cleanup), " : "") + "idles restart once the cancel ended";
            }
            bool on = ShortcutOn("bookhand");
            int seen = c[876] - sBhSeen0, fixedN = c[877] - sBhFixed0;
            sBhSim = null; sBhMode = null;
            string st = sim.FullName + ": switch " + on + ", stops " + sBhStops + ", stress interrupts " + sBhInterrupts + ", probes " + sBhProbes + " stuck " + sBhStuckProbes
                + ", put-downs seen +" + seen + " fixed +" + fixedN + ", cured " + sBhCured;
            if (sBhStops == 0) throw new InvalidOperationException("bookhand " + what + ": no read ended through StopUsingBook; " + st);
            if (what == "expect off") return (sBhStuckProbes > 0 || seen > 0 ? "stuck state occurred; " : "stuck state not seen; ") + st;
            if (sBhStuckProbes > 0 || fixedN < seen) throw new InvalidOperationException("bookhand expect on: wrong outcome; " + st);
            return (seen > 0 ? "as expected; " : "N/A (not triggered); ") + st;
        }

        static string BhRead(Sim sim)
        {
            Book book = null;
            foreach (Book b in sim.Inventory.FindAll<Book>(false)) { book = b; break; }
            if (book == null) throw new InvalidOperationException("bookhand read: " + sim.FullName + " has no book in the inventory");
            InteractionInstance ii = ReadBook.FromSimInventorySingleton.CreateInstance(book, sim, new InteractionPriority(InteractionPriorityLevel.High), false, true);
            return sim.FullName + ": ReadBook queued " + sim.InteractionQueue.Add(ii) + " on \"" + book.Title + "\"";
        }

        // Crowd form (repro book-hand-fix-check, Rodney 2026-11-12: one Sim reaching the stuck state is enough), args
        // "crowd <read N <household Sim>|state|stop|probe|expect off|expect on>":
        //   "read N <name>": the named household Sim reads a book from the inventory (a stuck book from before put away first,
        //            test cleanup) plus up to N non-household Sims at a bookshelf on their own home lot (bookcarry's crowd,
        //            BcCrowdRead: never the active household, its lot or a selectable Sim). Arms the crowd, takes the counters.
        //   "state": BhCrowdState; ready = reading with the book in hand and a carry machine.
        //   "stop":  stress (labelled, not a player action, "plant"): cancels every crowd read; when EA's StopUsingBook returns
        //            with the book already in the inventory (event 100 ran), BhStopAfter puts it back in the right hand. That is
        //            EA's stuck state (book in hand, parented, Sim.CarryStateMachine null, which EA already set) after a read that
        //            ended, so the read's own end runs EA's InteractionQueue.PutDownCarriedObjects on it at once, as in the natural
        //            hits (the error at the next interaction end after a read). animstop is not used here: it left StopUsingBook
        //            waiting in RequestState for good (bp-bookhand-fix-off 2026-11-12: 14 of 15 stuck still in ReadBook), so no
        //            put-down ever ran.
        //   "probe": which crowd Sims have a stuck book now (recorded; "stuck N of M").
        //   A Sim counts as stuck when a probe finds it stuck or when a put-down met its stuck book (BhAt).
        //   "expect off": throws unless >= 1 Sim was stuck and >= 1 put-down that throws reached a stuck book (bookhand_seen).
        //   "expect on":  throws unless >= 1 Sim was stuck, seen >= 1, every seen put-down fixed and no Sim stuck now. Disarms.
        public class BhCrowdState { public int sims, reading, ready, stuck, stops, seen, fixedN; public bool any; public string current; }

        static Dictionary<Sim, bool> sBhCrowd; // armed Sims -> reached the stuck state (probe or put-down)
        static Sim sBhHh;

        // Called by BookCarryHook.StopHook after EA's StopUsingBook returned ("plant" stress, crowd check only).
        static void BhStopAfter(Sim sim, Book book, bool putInInventory)
        {
            if (sBhMode != "plant" || sBhCrowd == null || !sBhCrowd.ContainsKey(sim)) return;
            if (!putInInventory || book == null || book.Parent == sim || sim.CarryStateMachine != null || !sim.Inventory.Contains(book)) return;
            if (!sim.Inventory.TryToRemove(book)) return;
            book.ParentToSlot(sim, Sim.ContainmentSlots.RightHand);
            sBhInterrupts++;
        }

        static object BhCrowdCheck(string arg)
        {
            int[] c = PatchCounters();
            if (arg.StartsWith("read "))
            {
                string rest = arg.Substring(5);
                int sp = rest.IndexOf(' ');
                if (sp < 0) throw new InvalidOperationException("bookhand crowd read N <household Sim full name>");
                Sim hh = BcSim(rest.Substring(sp + 1));
                sBhSim = null; sBhMode = null; sBhStops = sBhInterrupts = sBhProbes = sBhStuckProbes = sBhCured = 0; sBhSeen0 = c[876]; sBhFixed0 = c[877];
                string sum;
                Dictionary<Sim, int> picked = BcCrowdRead(int.Parse(rest.Substring(0, sp)), out sum);
                sBhCrowd = new Dictionary<Sim, bool>();
                foreach (Sim s in picked.Keys) sBhCrowd[s] = false;
                // test cleanup: any book in her hand first, with or without a carry machine. Baseline-BP loads Tiara holding a book
                // with a carry machine; cancelling her BedSleep over it made EA queue PutInInventory, which failed and threw
                // "left an object in the Sim's hand ... Attempting to put down that object failed" (bp-bookhand-fix-off 2026-11-12)
                Book held = BhBook(hh);
                if (held != null && held.Parent == hh) { held.PutBookInInventoryHandler(null, null); sBhCured++; }
                hh.InteractionQueue.CancelAllInteractions();
                string hr;
                try { hr = BhRead(hh); sBhCrowd[hh] = false; sBhHh = hh; }
                catch (Exception ex) { hr = ex.Message; sBhHh = null; } // the crowd still runs without the household Sim
                return "crowd: " + sum + "; household " + hr + "; switch bookhand " + ShortcutOn("bookhand");
            }
            if (sBhCrowd == null) throw new InvalidOperationException("bookhand crowd " + arg + ": no crowd armed");
            List<Sim> sims = new List<Sim>(sBhCrowd.Keys);
            if (arg == "state")
            {
                BhCrowdState cs = new BhCrowdState();
                Dictionary<string, int> cur = new Dictionary<string, int>();
                foreach (Sim s in sims)
                {
                    cs.sims++;
                    if (s.HasBeenDestroyed) continue;
                    bool reading = s.CurrentInteraction is ReadBook;
                    if (reading) cs.reading++;
                    if (reading && s.CarryStateMachine != null && BhBook(s) != null) cs.ready++;
                    if (BhStuck(s)) cs.stuck++;
                    string n0 = s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "nothing";
                    int n; cur.TryGetValue(n0, out n); cur[n0] = n + 1;
                }
                cs.stops = sBhStops; cs.seen = c[876] - sBhSeen0; cs.fixedN = c[877] - sBhFixed0;
                cs.any = cs.ready > 0;
                cs.current = "";
                foreach (KeyValuePair<string, int> e in cur) cs.current += (cs.current.Length > 0 ? ", " : "") + e.Key + " " + e.Value;
                return cs;
            }
            if (arg == "stop")
            {
                sBhMode = "plant";
                int stopped = 0, inHand = 0;
                foreach (Sim s in sims)
                {
                    if (s.HasBeenDestroyed || !(s.CurrentInteraction is ReadBook)) continue;
                    if (BhBook(s) != null) inHand++;
                    s.InteractionQueue.CancelAllInteractions();
                    stopped++;
                }
                if (stopped == 0) throw new InvalidOperationException("bookhand crowd stop: no crowd Sim is reading");
                return "crowd: " + stopped + " of " + sims.Count + " reads cancelled (" + inHand + " with the book in hand), plant armed";
            }
            if (arg == "probe")
            {
                sBhMode = null;
                int stuck = 0;
                string who = "";
                foreach (Sim s in sims)
                {
                    if (s.HasBeenDestroyed || !BhStuck(s)) continue;
                    stuck++; sBhCrowd[s] = true;
                    who += (who.Length > 0 ? "; " : "") + BhWho(s);
                }
                sBhProbes++; sBhStuckProbes += stuck;
                return "crowd: stuck " + stuck + " of " + sims.Count + (stuck > 0 ? ": " + who : "");
            }
            if (arg == "expect off" || arg == "expect on")
            {
                int stuckEver = 0, stuckNow = 0;
                foreach (Sim s in sims)
                {
                    if (sBhCrowd[s]) stuckEver++;
                    if (!s.HasBeenDestroyed && BhStuck(s)) stuckNow++;
                }
                int seen = c[876] - sBhSeen0, fixedN = c[877] - sBhFixed0;
                bool hhStuck = sBhHh != null && sBhCrowd.ContainsKey(sBhHh) && sBhCrowd[sBhHh];
                string st = "switch " + ShortcutOn("bookhand") + ", stuck " + stuckEver + " of " + sims.Count + " Sims (household Sim " + (sBhHh == null ? "not in the crowd" : hhStuck ? "stuck" : "not stuck")
                    + "), stuck now " + stuckNow + ", stops " + sBhStops + ", stress interrupts " + sBhInterrupts + ", put-downs seen +" + seen + " fixed +" + fixedN + ", cured " + sBhCured;
                sBhCrowd = null; sBhHh = null; sBhMode = null;
                if (stuckEver == 0) throw new InvalidOperationException("bookhand crowd " + arg + ": no Sim reached the stuck state; " + st);
                if (arg == "expect off")
                {
                    if (seen == 0) throw new InvalidOperationException("bookhand crowd expect off: no put-down that throws reached a stuck book; " + st);
                    return "bug shown; " + st;
                }
                if (seen == 0 || fixedN < seen || stuckNow > 0) throw new InvalidOperationException("bookhand crowd expect on: wrong outcome; " + st);
                return "as expected; " + st;
            }
            return "bookhand crowd <read N name|state|stop|probe|expect off|expect on>";
        }
    }
}
