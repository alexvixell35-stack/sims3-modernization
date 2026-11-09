using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
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
                string bhCtx = null;
                try { if (Actor != null) bhCtx = BhStopStart(Actor); } catch (Exception) { } // bookhand logging (BookHand.cs, docs/251)
                bool r = StopOrig(Actor, smcRead, bPutInInventory);
                try { if (Actor != null) BhStopEnd(Actor, bhCtx); } catch (Exception) { }
                try
                {
                    if (BookCarryCore.Dropped(before, smcRead, Actor.CarryStateMachine))
                    {
                        PatchCounters()[2043]++;
                        bool inCrowd = sBcCrowd != null && sBcCrowd.ContainsKey(Actor);
                        bool armed = (sBcSim != null && Actor == sBcSim) || inCrowd; // the agent check's Sims; kept only while armed
                        if (armed) { sBcLast = before; sBcSeen++; if (inCrowd) sBcCrowd[Actor]++; }
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
        static Dictionary<Sim, int> sBcCrowd; // "crowd" check: armed Sims -> carry machines dropped since "crowd read"

        // What "state" returns (reflect_call describes its fields, so a repro "until" can poll them).
        public class BcState { public bool reading, carry, ready; public int dropped; public string current, posture, queue; }
        public class BcCrowdState { public int sims, reading, carry, ready, dropped, droppedSims, idle; public bool any; public string current; }

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
            if (arg.StartsWith("crowd ")) return BcCrowdCheck(arg.Substring(6));
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
                // Repro rule: cancel first so autonomy (e.g. leaving via a locked door) cannot block the read; queue is then empty, so ReadBook runs at head.
                sim.InteractionQueue.CancelAllInteractions();
                InteractionInstance ii = ReadBook.FromSimInventorySingleton.CreateInstance(book, sim, new InteractionPriority(InteractionPriorityLevel.High), false, true);
                bool queued = sim.InteractionQueue.Add(ii);
                sBcSim = sim; sBcCrowd = null; sBcLast = null; sBcSeen = 0; sBcEnded = 0;
                return name + ": ReadBook queued " + queued + " on \"" + book.Title + "\"";
            }
            if (what == "state")
            {
                BcState bs = new BcState();
                bs.reading = sim.CurrentInteraction is ReadBook;
                bs.current = sim.CurrentInteraction != null ? sim.CurrentInteraction.GetType().Name : "nothing";
                bs.carry = sim.CarryStateMachine != null;
                bs.dropped = sBcSim == sim ? sBcSeen : 0;
                bs.posture = sim.Posture != null ? sim.Posture.GetType().Name : "none";
                bs.queue = "";
                foreach (InteractionInstance q in sim.InteractionQueue.InteractionList) bs.queue += (bs.queue.Length > 0 ? "," : "") + q.GetType().Name;
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
            if (sBcSim != sim) throw new InvalidOperationException("bookcarry " + what + ": no read armed on " + name);
            int seen = sBcSeen, ended = sBcEnded;
            StateMachineClient m = sBcLast;
            sBcSim = null; sBcLast = null;
            return BcExpect(what, name, seen, ended, m);
        }

        static object BcExpect(string what, string who, int seen, int ended, StateMachineClient m)
        {
            bool on = ShortcutOn("bookcarry");
            bool valid = false; try { valid = m != null && m.IsValid; } catch (Exception) { }
            long h = m != null ? BcHandle(m) : -1;
            bool ok = seen >= 1 && m != null && (what == "expect left" ? !on && ended == 0 && h != 0 : on && ended == seen && h == 0);
            string st = who + ": seen +" + seen + ", ended +" + ended + ", dropped machine " + (m == null ? "none" : "driver " + (h != 0 ? "held" : "released") + ", event queue valid " + valid);
            if (!ok) throw new InvalidOperationException("bookcarry " + what + ": wrong outcome (switch " + on + "); " + st);
            return "as expected; " + st;
        }

        // Crowd form of the check (args "crowd <read N|state|stop|expect left|expect ended>"): the carry machine is dropped only
        // when the book goes to the reader's inventory, i.e. a read on the reader's own home lot (ReadBook.ShouldPutInInventory),
        // so each Sim reads from a bookshelf on its own home lot. Never the active household, its lot or a selectable Sim.
        //   "read N": up to N Sims, at most 2 per shelf: cancel their queues, max motives, Teleport Me Here in front of the shelf
        //             (skipped when already within 3 m on that lot), then EA's Bookshelf_ReadSomething at high priority. Arms the crowd.
        //   "state":  counts (BcCrowdState); any = a crowd Sim reads with the book in hand, or a crowd read already dropped.
        //   "stop":   cancels the reads of crowd Sims that are reading, so EA ends them through StopUsingBook.
        //   "expect left|ended": as the one-Sim form, over all crowd drops (the last dropped machine checked). Disarms.
        static object BcCrowdCheck(string arg)
        {
            if (arg.StartsWith("read "))
            {
                int max = int.Parse(arg.Substring(5));
                Household active = Household.ActiveHousehold;
                Lot activeLot = active != null ? active.LotHome : null;
                Dictionary<Bookshelf, int> perShelf = new Dictionary<Bookshelf, int>();
                Dictionary<Sim, int> crowd = new Dictionary<Sim, int>();
                List<string> lots = new List<string>();
                int refused = 0;
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (crowd.Count >= max) break;
                    SimDescription sd = s.SimDescription;
                    Lot home = s.LotHome;
                    if (sd == null || s.HasBeenDestroyed || s.IsSelectable || s.Household == null || s.Household == active || home == null || home == activeLot) continue;
                    if (!sd.IsHuman || !sd.TeenOrAbove || s.Parent != null) continue;
                    Bookshelf shelf = null;
                    foreach (Bookshelf b in home.GetObjects<Bookshelf>())
                    {
                        int n; perShelf.TryGetValue(b, out n);
                        if (n < 2 && b.Inventory != null && b.Inventory.Find<BookGeneral>() != null) { shelf = b; break; }
                    }
                    if (shelf == null) continue;
                    s.InteractionQueue.CancelAllInteractions();
                    foreach (CommodityKind k in new CommodityKind[] { CommodityKind.Hunger, CommodityKind.Bladder, CommodityKind.Energy, CommodityKind.Hygiene, CommodityKind.Social, CommodityKind.Fun })
                        if (s.Motives != null && s.Motives.HasMotive(k)) s.Motives.SetMax(k);
                    Vector3 spot = shelf.Position + shelf.ForwardVector * 1.2f;
                    float dx = s.Position.x - spot.x, dz = s.Position.z - spot.z;
                    if (s.LotCurrent != home || dx * dx + dz * dz > 9f)
                    {
                        // EA's own teleport, as the agent's setup_sims (a bare SetPosition leaves LotCurrent on the old lot)
                        TerrainInteraction tp = new Terrain.TeleportMeHere.Definition(false).CreateInstance(Terrain.Singleton, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true) as TerrainInteraction;
                        typeof(TerrainInteraction).GetField("Destination", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(tp, spot);
                        if (!s.InteractionQueue.Add(tp)) { refused++; continue; }
                    }
                    if (!s.InteractionQueue.Add(Bookshelf_ReadSomething.Singleton.CreateInstance(shelf, s, new InteractionPriority(InteractionPriorityLevel.High), false, true))) { refused++; s.InteractionQueue.CancelAllInteractions(); continue; }
                    crowd[s] = 0;
                    int had; perShelf.TryGetValue(shelf, out had); perShelf[shelf] = had + 1;
                    if (!lots.Contains(home.Name)) lots.Add(home.Name);
                }
                if (crowd.Count == 0) throw new InvalidOperationException("bookcarry crowd read: no non-household Sim with a bookshelf (with a book) on its home lot; refused " + refused);
                sBcCrowd = crowd; sBcSim = null; sBcLast = null; sBcSeen = 0; sBcEnded = 0;
                return "crowd: " + crowd.Count + " Sims at " + perShelf.Count + " shelves on " + lots.Count + " lots (" + string.Join(", ", lots.ToArray()) + "), refused " + refused;
            }
            if (sBcCrowd == null) throw new InvalidOperationException("bookcarry crowd " + arg + ": no crowd armed");
            if (arg == "state")
            {
                BcCrowdState cs = new BcCrowdState();
                Dictionary<string, int> cur = new Dictionary<string, int>();
                foreach (KeyValuePair<Sim, int> e in sBcCrowd)
                {
                    Sim s = e.Key;
                    cs.sims++;
                    if (e.Value > 0) { cs.dropped += e.Value; cs.droppedSims++; }
                    if (s.HasBeenDestroyed) continue;
                    bool reading = s.CurrentInteraction is ReadBook, carry = s.CarryStateMachine != null;
                    if (reading) cs.reading++;
                    if (carry) cs.carry++;
                    if (reading && carry) cs.ready++;
                    string c = s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "nothing";
                    if (c == "nothing") cs.idle++;
                    int n; cur.TryGetValue(c, out n); cur[c] = n + 1;
                }
                cs.any = cs.ready > 0 || cs.droppedSims > 0;
                cs.current = "";
                foreach (KeyValuePair<string, int> e in cur) cs.current += (cs.current.Length > 0 ? ", " : "") + e.Key + " " + e.Value;
                return cs;
            }
            if (arg == "stop")
            {
                int stopped = 0, carrying = 0;
                foreach (Sim s in sBcCrowd.Keys)
                {
                    if (s.HasBeenDestroyed || !(s.CurrentInteraction is ReadBook)) continue;
                    if (s.CarryStateMachine != null) carrying++;
                    s.InteractionQueue.CancelAllInteractions();
                    stopped++;
                }
                if (stopped == 0 && sBcSeen == 0) throw new InvalidOperationException("bookcarry crowd stop: no crowd Sim is reading and no read ended");
                return "crowd: " + stopped + " reads cancelled (" + carrying + " with carry), dropped so far +" + sBcSeen;
            }
            if (arg == "expect left" || arg == "expect ended")
            {
                int hit = 0;
                foreach (int v in sBcCrowd.Values) if (v > 0) hit++;
                int sims = sBcCrowd.Count, seen = sBcSeen, ended = sBcEnded;
                StateMachineClient m = sBcLast;
                sBcCrowd = null; sBcLast = null;
                return BcExpect(arg, "crowd " + hit + " of " + sims + " Sims dropped", seen, ended, m);
            }
            return "bookcarry crowd <read N|state|stop|expect left|expect ended>";
        }
    }
}
