using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Seating;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // doorseat / doorseathh (docs/686 §39, default off). Community report: "objects becoming inaccessible when placed too
    // close to a door/archway ... even when you place tables without MoveObjects, not all chairs are usable."
    //
    // [verified, decompiled ChairDining.RouteToForSitting] Before a Sim sits on a dining chair the game asks the build-mode
    // placement test World.TestObjectPlacement two questions: a chair on its own, CanEnableFeetFootprint (its feet area
    // switched on, other Sims allowed); a chair at a table, after the walk, CanScootBack (the chair where it stands with its
    // centre footprint off: may it pull out). A refused feet test leaves the route with no destination
    // (FailedBecauseNoDestinationSpecified, the chair as blocker), a refused scoot test plays the route-fail shrug at the
    // chair. The placement test counts the keep-clear area of a door or arch, the area that keeps furniture out of a
    // doorway; build mode placed the chair with its feet and scoot areas off, so the chair stands and cannot be used.
    // The bar stool asks the same scoot test (ChairBarStool.CanScootBack, 0.75 m back).
    //
    // Fix: when the game refuses one of these tests for a Sim (not the build-mode / load calls, which pass no Sim) and a
    // door stands within kDsReach, the test is made again with that door allowed to intersect (the chair's own
    // mAllowIntersectionObject hook, which the game uses for the Sim and the table), other Sims allowed as well; when it
    // passes the Sim sits. Walls, other furniture and the table still count. A Sim sitting there may stand in the door's
    // keep-clear area; walkers clip past (the player allows clipping at portals). Ordinary walks are unchanged.
    // Scope: doorseat = Sims the player does not control, doorseathh = household and selected Sims (each alone). Humans.
    // Off = compare mode: counted, nothing done. Counters 4980-4999. Check: DoorSeatCheck.
    public static partial class Fixes
    {
        const int kDsTests = 4980, kDsWould = 4981, kDsScootFixed = 4982, kDsFeetFixed = 4983, kDsNoDoor = 4984, kDsKept = 4985,
            kDsExempt = 4986, kDsErrors = 4987, kDsScootRefused = 4988, kDsFeetRefused = 4989, kSrFaded = 4990, kDsLines = 4991,
            kSrCases = 4995, kSrGraceWalked = 4996, kSrClip = 4997, kSrSnap = 4998, kSrTestAllowed = 4999;
        const int kSrGraceMs = 2500;  // real time the Sim waits at a closed seat before the clip walk (the slot may open)
        const float kDsReach = 3f;   // doors this close to the chair (flat, metres) may be allowed

        static readonly LineCapCore sDsCap = new LineCapCore(20, 60000, 300);
        static FieldInfo sDsChairAllow, sDsChairAllSims, sDsStoolAllow;
        static string sDsMiss = "", sDsLast = "", sDsLastError = "";

        static void ApplyDoorSeat()
        {
            sDsMiss = "";
            BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic;
            sDsChairAllow = typeof(ChairDining).GetField("mAllowIntersectionObject", bf);
            sDsChairAllSims = typeof(ChairDining).GetField("mAllowIntersectionAllSims", bf);
            sDsStoolAllow = typeof(ChairBarStool).GetField("mAllowIntersectionObject", bf);
            if (sDsChairAllow == null || sDsChairAllSims == null) sDsMiss += "ChairDining.mAllowIntersection* ";
            if (sDsStoolAllow == null) sDsMiss += "ChairBarStool.mAllowIntersectionObject ";
            if (!Patch(typeof(ChairDining), "CanScootBack", new Type[] { typeof(Sim) }, typeof(DsChairHook), "ScootHook", "ScootOrig")) sDsMiss += "ChairDining.CanScootBack ";
            if (!Patch(typeof(ChairDining), "CanEnableFeetFootprint", new Type[] { typeof(Sim), typeof(bool) }, typeof(DsChairHook), "FeetHook", "FeetOrig")) sDsMiss += "ChairDining.CanEnableFeetFootprint ";
            if (!Patch(typeof(ChairBarStool), "CanScootBack", new Type[] { typeof(Sim) }, typeof(DsStoolHook), "ScootHook", "ScootOrig")) sDsMiss += "ChairBarStool.CanScootBack ";
            sDsMiss = sDsMiss.Trim();
            if (sDsMiss.Length > 0) AppliedLog().Add("doorseat: not found: " + sDsMiss);
        }

        public abstract class DsChairHook : ChairDining
        {
            public bool ScootHook(Sim s)
            {
                bool r = ScootOrig(s);
                return r || DsRetry(this, s, 0);
            }
            public bool ScootOrig(Sim s) { return false; }

            public bool FeetHook(Sim s, bool considerSimsAsBlocking)
            {
                bool r = FeetOrig(s, considerSimsAsBlocking);
                return r || DsRetry(this, s, 1);
            }
            public bool FeetOrig(Sim s, bool considerSimsAsBlocking) { return false; }
        }

        public abstract class DsStoolHook : ChairBarStool
        {
            public bool ScootHook(Sim s)
            {
                bool r = ScootOrig(s);
                return r || DsRetry(this, s, 2);
            }
            public bool ScootOrig(Sim s) { return false; }
        }

        static readonly string[] kDsTestNames = { "scoot", "feet", "stool scoot" };

        // The game refused test `which` (0 chair scoot, 1 chair feet, 2 stool scoot) of `chair` for Sim s. True = allowed.
        static bool DsRetry(GameObject chair, Sim s, int which)
        {
            if (s == null || chair == null) return false;
            try
            {
                List<CommonDoor> doors = DsDoorsNear(chair);
                if (!DoorSeatCore.Kind(false, true, doors.Count))
                {
                    PatchCounters()[kDsNoDoor]++;
                    return SrTestFallback(chair, s, which, "no door near");
                }
                int[] c = PatchCounters();
                c[kDsTests]++;
                c[which == 1 ? kDsFeetRefused : kDsScootRefused]++;
                bool human = s.SimDescription != null && s.SimDescription.IsHuman;
                int d = DoorSeatCore.Decide(true, human, IsPlayerSim(s), ShortcutOn("doorseat"), ShortcutOn("doorseathh"));
                if (d == DoorSeatCore.kExempt) { c[kDsExempt]++; return SrTestFallback(chair, s, which, "doorseat exempt"); }
                bool[] pass = new bool[doors.Count];
                for (int i = 0; i < doors.Count; i++) pass[i] = DsTest(chair, which, doors[i], true);
                int k = DoorSeatCore.FirstPass(pass, doors.Count);
                string what = BuWho(s) + " " + s.FullName + " -> " + chair.GetType().Name + " 0x" + chair.ObjectId.Value.ToString("X") + " " + kDsTestNames[which];
                if (k < 0) { c[kDsKept]++; DsLine(what + ": refused, a door allowed still refused (" + doors.Count + " doors near): kept"); return SrTestFallback(chair, s, which, "a door allowed still refused"); }
                string door = doors[k].GetType().Name + " " + BuFlat(doors[k].Position, chair.Position).ToString("0.00") + " m";
                if (d != DoorSeatCore.kGo) { c[kDsWould]++; DsLine(what + ": refused by door " + door + ": would allow"); return SrTestFallback(chair, s, which, "doorseat off"); }
                c[which == 1 ? kDsFeetFixed : kDsScootFixed]++;
                DsLine(what + ": refused by door " + door + ": allowed");
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { DsError(e); return false; }
        }

        static void DsLine(string text)
        {
            sDsLast = text;
            PatchCounters()[kDsLines]++;
            if (sDsCap.Take(System.Environment.TickCount)) DiagLine("doorseat: " + text);
        }

        static List<CommonDoor> DsDoorsNear(GameObject chair)
        {
            List<CommonDoor> r = new List<CommonDoor>();
            Vector3 p = chair.Position;
            foreach (CommonDoor d in Sims3.Gameplay.Queries.GetObjects<CommonDoor>(p, kDsReach + 1f))
            {
                if (d == null || !d.InWorld) continue;
                if (DoorSeatCore.DoorNear(BuFlat(d.Position, p), d.Position.y - p.y, kDsReach)) r.Add(d);
            }
            return r;
        }

        // The game's test of `which`, with `allow` (may be null) allowed to intersect; allSims also lets every Sim through
        // (the feet test does that already; the scoot test lets only the sitting Sim through, the retry all Sims, since the
        // Sim it named is not passed). Footprints are put back as the game leaves them.
        static bool DsTest(GameObject chair, int which, Sims3.Gameplay.Interfaces.IGameObject allow, bool allSims)
        {
            if (which == 2)
            {
                if (sDsStoolAllow == null) return false;
                ChairBarStool st = (ChairBarStool)chair;
                sDsStoolAllow.SetValue(st, allow);
                try { return World.TestObjectPlacement(st.ObjectId, TestObjectPlacementBooleans.TemporaryPlacement, st.Position - st.ForwardVector * 0.75f, st.ForwardVector); }
                finally { sDsStoolAllow.SetValue(st, null); }
            }
            if (sDsChairAllow == null || sDsChairAllSims == null) return false;
            ChairDining ch = (ChairDining)chair;
            uint feet = ResourceUtils.HashString32("ftp_Feet");
            if (which == 0)
            {
                bool feetOn = ch.IsFootprintEnabled(feet);
                ch.DisableFootprint("ftp_Center");
                if (feetOn) ch.DisableFootprint(feet);
                sDsChairAllow.SetValue(ch, allow);
                sDsChairAllSims.SetValue(ch, allSims);
                try { return World.TestObjectPlacement(ch.ObjectId, TestObjectPlacementBooleans.TemporaryPlacement, ch.Position, ch.ForwardVector); }
                finally
                {
                    sDsChairAllow.SetValue(ch, null);
                    sDsChairAllSims.SetValue(ch, false);
                    ch.EnableFootprint("ftp_Center");
                }
            }
            if (ch.IsParentedToSurface()) return false;
            ch.EnableFootprint(feet);
            sDsChairAllow.SetValue(ch, allow);
            sDsChairAllSims.SetValue(ch, true);
            try { return World.TestObjectPlacement(ch.ObjectId, TestObjectPlacementBooleans.TemporaryPlacement, ch.Position); }
            finally
            {
                sDsChairAllow.SetValue(ch, null);
                sDsChairAllSims.SetValue(ch, false);
                ch.DisableFootprint(feet);
            }
        }

        // ---- seatreach / seatreachhh / seatsnap (docs/686 §39.4): any seat the Sim cannot reach ----
        // A player's wish: "if sims cant reach the table chair they could just teleport to the interaction maybe like the
        // bed thing ... or walk there clipping through things". Order: (1) the game's own approach slots; (2) after a
        // grace of kSrGraceMs a normal plan again, then blockeduse's walk through objects (clip) to the slot; (3) only when
        // no such walk exists and seatsnap is on, the snap onto the slot, behind a fade when the Sim or the slot is on
        // screen. Seats: dining chairs, bar stools and every object with a Sittable (sofas, loveseats, living chairs,
        // benches). The chair's own seat tests (pull out, feet area) refused by anything but a Sim's own slot are
        // allowed for these Sims after doorseat's door retry: the chair clips into what refused it.
        static bool SrIsSeat(GameObject o)
        {
            return o is ChairDining || o is ChairBarStool || o.Sittable != null;
        }

        static bool SrOnFor(Sim s)
        {
            bool human = s.SimDescription != null && s.SimDescription.IsHuman;
            return DoorSeatCore.Decide(true, human, IsPlayerSim(s), ShortcutOn("seatreach"), ShortcutOn("seatreachhh")) == DoorSeatCore.kGo;
        }

        // blockeduse's BuBegin: a failed plan to a seat's slots that blockeduse's own switches leave to the game
        static bool SrSeatCase(Sim sim, GameObject obj)
        {
            try
            {
                if (!ShortcutOn("seatreach") && !ShortcutOn("seatreachhh")) return false;
                if (obj == null || !SrIsSeat(obj) || !SrOnFor(sim)) return false;
                PatchCounters()[kSrCases]++;
                DsLine("seatreach: " + BuWho(sim) + " " + sim.FullName + " -> " + obj.GetType().Name + " 0x" + obj.ObjectId.Value.ToString("X") + ": seat slot route failed, grace then clip" + (ShortcutOn("seatsnap") ? " / snap" : ""));
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { DsError(e); return false; }
        }

        // The grace: the Sim stands kSrGraceMs, then the route is planned again as the game planned it. True = it
        // succeeded (r holds that plan, the caller walks it). A cancelled or reset Sim keeps the failed plan.
        static bool SrGrace(Sim sim, Route r, BuCase k)
        {
            int t0 = System.Environment.TickCount;
            for (int i = 0; i < 2000 && System.Environment.TickCount - t0 < kSrGraceMs; i++)
            {
                Simulator.Sleep(10);
                if (sim.HasBeenDestroyed || sim.HasExitReason()) return false;
            }
            RoutePlanResult pr = r.Plan();
            if (!pr.Succeeded()) { r.PlanResult = k.Saved; return false; }
            PatchCounters()[kSrGraceWalked]++;
            DsLine("seatreach: " + sim.FullName + " -> " + k.Obj.GetType().Name + " 0x" + k.Obj.ObjectId.Value.ToString("X") + ": slot open after the grace, walks");
            return true;
        }

        static bool SrFadeWanted(Sim sim, Vector3 pos)
        {
            try { return UcOnScreen(sim) || !FarFromCamera(pos) || !FarFromCamera(sim.Position); }
            catch (ResetException) { throw; }
            catch (Exception) { return true; }
        }

        static void SrSnapped(bool faded)
        {
            int[] c = PatchCounters();
            c[kSrSnap]++;
            if (faded) c[kSrFaded]++;
        }

        // A seat test the game and doorseat refused, for a Sim seatreach covers: allowed (the chair clips).
        static bool SrTestFallback(GameObject chair, Sim s, int which, string why)
        {
            if (!ShortcutOn("seatreach") && !ShortcutOn("seatreachhh")) return false;
            if (!SrOnFor(s)) return false;
            PatchCounters()[kSrTestAllowed]++;
            DsLine("seatreach: " + BuWho(s) + " " + s.FullName + " -> " + chair.GetType().Name + " 0x" + chair.ObjectId.Value.ToString("X") + " " + kDsTestNames[which] + " refused (" + why + "): allowed, the chair clips");
            return true;
        }

        static void DsError(Exception e)
        {
            PatchCounters()[kDsErrors]++;
            sDsLastError = "doorseat: error: " + e.GetType().Name + ": " + e.Message;
            if (PatchCounters()[kDsErrors] <= 3) DiagLine(sDsLastError);
        }

        static string DsCounters()
        {
            int[] c = PatchCounters();
            return "doorseat counters: refused with a door near " + c[kDsTests] + " (scoot " + c[kDsScootRefused] + ", feet " + c[kDsFeetRefused]
                + "), would " + c[kDsWould] + ", allowed scoot " + c[kDsScootFixed] + " / feet " + c[kDsFeetFixed]
                + ", kept: door allowed still refused " + c[kDsKept] + ", refused, no door near " + c[kDsNoDoor] + ", exempt " + c[kDsExempt]
                + "; seatreach: seat cases " + c[kSrCases] + ", walked after the grace " + c[kSrGraceWalked] + ", clip " + c[kSrClip] + ", snap " + c[kSrSnap] + " (faded " + c[kSrFaded] + "), seat test allowed " + c[kSrTestAllowed]
                + "; check: sit orders " + c[4992] + ", seated " + c[4993] + ", not seated " + c[4994] + ", errors " + c[kDsErrors] + ", lines " + c[kDsLines]
                + "; last: " + sDsLast + (sDsLastError.Length > 0 ? "; " + sDsLastError : "") + (sDsMiss.Length > 0 ? "; missing: " + sDsMiss : "");
        }

        // ---- check ----
        // DoorSeatCheck run: the rules with controls (read-only). result: counters and the last line. lot: every seat of the
        // selected Sim's lot (dining chairs, bar stools, sofas, living chairs): the game's seat tests, which nearby object
        // alone lets a refused test pass (the blocker), every routing slot's plan for the selected Sim (result, blocker)
        // and where the slot lies against the nearest door (door-local forward / side metres). sit <i> [auto]: the
        // selected Sim is ordered (or as its own pick) to sit on seat i of that list; seated: who sits where now (counts
        // the outcome of the last order).
        static readonly List<GameObject> sDsSeats = new List<GameObject>();
        static GameObject sDsOrdered;

        public static object DoorSeatCheck(string what)
        {
            try
            {
                if (what == "result") return DsCounters();
                if (what == "lot") return DsLot();
                if (what == "seated") return DsSeated();
                if (what == "furnish" || what == "furnish all") return DsFurnish(what == "furnish all");
                if (what != null && what.StartsWith("sit ")) return DsSit(what.Substring(4));
                if (what != "run") return "doorseat <run|result|lot|sit <i> [auto]|seated>";
                bool on = ShortcutOn("doorseat"), hh = ShortcutOn("doorseathh");
                const int G = DoorSeatCore.kGo;
                bool fixedNow = DoorSeatCore.Kind(false, true, 1) && DoorSeatCore.Decide(true, true, false, on, hh) == G
                    && DoorSeatCore.FirstPass(new bool[] { false, true }, 2) == 1;
                bool ctlKind = !DoorSeatCore.Kind(true, true, 1) && !DoorSeatCore.Kind(false, false, 1) && !DoorSeatCore.Kind(false, true, 0);
                bool ctlScope = DoorSeatCore.Decide(true, true, true, true, false) == DoorSeatCore.kExempt && DoorSeatCore.Decide(true, true, true, false, true) == G
                    && DoorSeatCore.Decide(true, false, false, true, true) == DoorSeatCore.kExempt && DoorSeatCore.Decide(true, true, false, false, false) == DoorSeatCore.kWould;
                bool ctlKeep = DoorSeatCore.FirstPass(new bool[] { false, false }, 2) == -1 && !DoorSeatCore.DoorNear(3.5f, 0f, kDsReach) && !DoorSeatCore.DoorNear(1f, 2.5f, kDsReach);
                bool controls = ctlKind && ctlScope && ctlKeep && sDsChairAllow != null;
                return "doorseat check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): a seat test refused by a door's keep-clear area is allowed=" + fixedNow
                    + "; control: passed / no Sim / no door kept=" + ctlKind + ", scope per switch=" + ctlScope + ", still refused or door far kept=" + ctlKeep
                    + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sDsMiss.Length == 0) + "; " + DsCounters();
            }
            catch (Exception e) { return "doorseat check error: " + e; }
        }

        // furnish (repro set-up): every free chair slot of every dining table on the selected Sim's lot gets a dining chair
        // the way the buy tool slots one: the chair's own buy-mode test HandToolChildAllowPlacementInSlot first (a slot it
        // refuses is one a player cannot fill without moveobjects: counted, left empty), then ParentToSlot and the hand
        // tool's placement callbacks. Chairs: the lot's loose dining chairs first, then copies of the first one.
        // "furnish all" also fills the slots the buy-mode test refuses: the end state of a door or arch built after the
        // chairs, or a table set moved next to a door (the door tool and a moved table do not test the chairs' pull-out
        // area [inferred, not verified in game]); each such chair is listed as "forced".
        static string DsFurnish(bool all)
        {
            Sim sel = PlumbBob.SelectedActor;
            if (sel == null || sel.LotCurrent == null) return "doorseat furnish: no selected Sim on a lot";
            Lot lot = sel.LotCurrent;
            List<ChairDining> loose = new List<ChairDining>();
            foreach (ChairDining c in lot.GetObjects<ChairDining>()) if (c.InWorld && c.Parent == null) loose.Add(c);
            if (loose.Count == 0) return "doorseat furnish: no loose dining chair on the lot to copy";
            ResourceKey key = loose[0].GetResourceKey();
            System.Text.StringBuilder b = new System.Text.StringBuilder("doorseat furnish:");
            int slotted = 0, refused = 0, made = 0;
            foreach (Sims3.Gameplay.Objects.TableDining t in lot.GetObjects<Sims3.Gameplay.Objects.TableDining>())
            {
                if (t.FoodSlotPairs == null) continue;
                b.Append(" table 0x" + t.ObjectId.Value.ToString("X") + " at " + BuAt(t.Position) + ":");
                foreach (Sims3.Gameplay.Objects.CookingObjects.SlotPair sp in t.FoodSlotPairs)
                {
                    if (t.IsSlotOccupied(sp.ChairSlot)) continue;
                    ChairDining ch = null;
                    Vector3 at = t.GetSlotPosition(sp.ChairSlot);
                    float best = 1e9f;
                    foreach (ChairDining c in loose) { float dd = BuFlat(c.Position, at); if (c.Parent == null && dd < best) { best = dd; ch = c; } }
                    bool fresh = false;
                    if (ch == null || loose.Count <= 1)
                    {
                        ch = GlobalFunctions.CreateObjectOutOfWorld(key) as ChairDining;
                        fresh = true;
                        if (ch == null) { b.Append(" no copy;"); continue; }
                        made++;
                    }
                    bool ok = ch.HandToolChildAllowPlacementInSlot(t, sp.ChairSlot);
                    if (!ok && all) b.Append(" " + sp.ChairSlot + " forced (buy mode refuses it):");
                    if (!ok && !all)
                    {
                        refused++;
                        b.Append(" " + sp.ChairSlot + " refused by the buy-mode test;");
                        if (fresh) ch.Destroy();
                        continue;
                    }
                    bool p = ch.ParentToSlot(t, sp.ChairSlot);
                    if (fresh) ch.AddToWorld();
                    ch.OnHandToolPlacementInSlot(t, sp.ChairSlot);
                    ch.OnHandToolPlacement();
                    loose.Remove(ch);
                    if (p) slotted++;
                    b.Append(" " + sp.ChairSlot + (p ? " chair 0x" + ch.ObjectId.Value.ToString("X") : " parent failed") + ";");
                }
            }
            return b.Append(" | slotted " + slotted + ", refused by buy mode " + refused + ", copies made " + made).ToString();
        }

        static bool DsIsSeat(GameObject o)
        {
            return o is ChairDining || o is ChairBarStool || o.Sittable != null;
        }

        static void DsListSeats(Lot lot)
        {
            sDsSeats.Clear();
            foreach (GameObject o in lot.GetObjects<GameObject>())
                if (o != null && o.InWorld && DsIsSeat(o) && !(o is Sim)) sDsSeats.Add(o);
            sDsSeats.Sort(delegate(GameObject a, GameObject b) { return a.ObjectId.Value.CompareTo(b.ObjectId.Value); });
        }

        static CommonDoor DsNearestDoor(Vector3 p, Lot lot, out float dist)
        {
            CommonDoor best = null;
            dist = 1e9f;
            foreach (CommonDoor d in lot.GetObjects<CommonDoor>())
            {
                if (d == null || !d.InWorld || Math.Abs(d.Position.y - p.y) > 1f) continue;
                float f = BuFlat(d.Position, p);
                if (f < dist) { dist = f; best = d; }
            }
            return best;
        }

        static string DsLocal(Vector3 p, CommonDoor d)
        {
            if (d == null) return "no door";
            float f, s;
            DoorSeatCore.DoorLocal(p.x - d.Position.x, p.z - d.Position.z, d.ForwardVector.x, d.ForwardVector.z, out f, out s);
            return "door f " + f.ToString("0.00") + " s " + s.ToString("0.00") + (DoorSeatCore.InClearArea(f, s, 0.75f, 1f) ? " (in front)" : "");
        }

        static string DsLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            if (sel == null || sel.LotCurrent == null) return "doorseat lot: no selected Sim on a lot";
            Lot lot = sel.LotCurrent;
            DsListSeats(lot);
            System.Text.StringBuilder b = new System.Text.StringBuilder("doorseat lot " + lot.Name + ":");
            foreach (CommonDoor d in lot.GetObjects<CommonDoor>())
                b.Append(" [door " + d.GetType().Name + " 0x" + d.ObjectId.Value.ToString("X") + " at " + BuAt(d.Position) + " fwd " + d.ForwardVector.x.ToString("0.0") + "," + d.ForwardVector.z.ToString("0.0") + "]");
            int seats = 0, slotsAll = 0, slotsBad = 0, testsBad = 0;
            for (int i = 0; i < sDsSeats.Count && i < 40; i++)
            {
                GameObject o = sDsSeats[i];
                seats++;
                float dd;
                CommonDoor door = DsNearestDoor(o.Position, lot, out dd);
                b.Append(" | #" + i + " " + o.GetType().Name + " 0x" + o.ObjectId.Value.ToString("X") + " at " + BuAt(o.Position)
                    + " parent " + (o.Parent != null ? o.Parent.GetType().Name : "none") + ", door " + (door != null ? dd.ToString("0.00") + " m" : "none") + " " + DsLocal(o.Position, door));
                int which = o is ChairBarStool ? 2 : o is ChairDining ? (((ChairDining)o).IsParentedToSurface() ? 0 : 1) : -1;
                if (which >= 0)
                {
                    bool ea = DsTest(o, which, sel, which == 1);  // the game's own test (scoot: only this Sim allowed)
                    b.Append("; " + kDsTestNames[which] + " test " + (ea ? "ok" : "REFUSED"));
                    if (!ea)
                    {
                        testsBad++;
                        string pass = "";
                        foreach (GameObject n in Sims3.Gameplay.Queries.GetObjects<GameObject>(o.Position, 3f))
                        {
                            if (n == null || n == o || n is Sim) continue;
                            if (DsTest(o, which, n, true)) pass += " " + n.GetType().Name + " 0x" + n.ObjectId.Value.ToString("X") + " " + BuFlat(n.Position, o.Position).ToString("0.00") + "m";
                        }
                        b.Append(", passes with only this allowed:" + (pass.Length > 0 ? pass : " none (a wall or several objects)"));
                    }
                }
                Slot[] rs = DsSlots(o, which);
                string sl = "";
                foreach (Slot s in rs)
                {
                    slotsAll++;
                    Route t = sel.CreateRoute();
                    t.DoRouteFail = false;
                    RoutePlanResult pr = t.PlanToSlot(o, s);
                    Vector3 sp = o.GetSlotPosition(s);
                    float sd;
                    CommonDoor sdoor = DsNearestDoor(sp, lot, out sd);
                    if (!pr.Succeeded()) slotsBad++;
                    sl += " " + s.ToString().Replace("RoutingSlot_", "rs") + " " + (pr.Succeeded() ? "ok" : pr.mType + " blk " + pr.mBlockerType + (pr.mBlockerObjectId != 0 ? " 0x" + pr.mBlockerObjectId.ToString("X") : "")) + " " + DsLocal(sp, sdoor) + ";";
                }
                b.Append("; slots:" + sl);
            }
            return b.Append(" | seats " + seats + ", seat tests refused " + testsBad + ", slots " + slotsAll + ", slot plans failed " + slotsBad).ToString();
        }

        static Slot[] DsSlots(GameObject o, int which)
        {
            if (which == 0) return new Slot[] { Slot.RoutingSlot_5, Slot.RoutingSlot_6 };
            if (which == 1) return new Slot[] { Slot.RoutingSlot_0, Slot.RoutingSlot_1, Slot.RoutingSlot_2, Slot.RoutingSlot_3, Slot.RoutingSlot_4 };
            List<Slot> r = new List<Slot>();
            SittableComponent sc = o.Sittable;
            if (sc != null && sc.PartDataList != null)
                foreach (SitData sd in sc.PartDataList.Values)
                    if (sd != null && sd.RoutingSlot != null)
                        foreach (Slot s in sd.RoutingSlot) if (!r.Contains(s)) r.Add(s);
            if (r.Count == 0) { Slot[] all = o.GetRoutingSlots(); if (all != null) r.AddRange(all); }
            return r.ToArray();
        }

        static string DsSit(string arg)
        {
            Sim sel = PlumbBob.SelectedActor;
            if (sel == null || sel.LotCurrent == null) return "doorseat sit: no selected Sim";
            if (sDsSeats.Count == 0) DsListSeats(sel.LotCurrent);
            string[] parts = arg.Trim().Split(' ');
            int i = int.Parse(parts[0]);
            bool auto = parts.Length > 1 && parts[1] == "auto";
            if (i < 0 || i >= sDsSeats.Count) return "doorseat sit: no seat " + i + " (" + sDsSeats.Count + " seats)";
            GameObject seat = sDsSeats[i];
            InteractionObjectPair sit = null;
            foreach (InteractionObjectPair iop in seat.Interactions)
            {
                Type dt = iop.InteractionDefinition.GetType();
                string n = (dt.DeclaringType != null ? dt.DeclaringType.Name : dt.Name);
                if (n == "Sit" || n == "SitOnCouch") { sit = iop; break; }
            }
            if (sit == null) return "doorseat sit: no Sit interaction on " + seat.GetType().Name;
            sel.InteractionQueue.CancelAllInteractions();
            InteractionPriority pri = new InteractionPriority(auto ? InteractionPriorityLevel.Autonomous : InteractionPriorityLevel.UserDirected);
            InteractionInstance ii = sit.InteractionDefinition.CreateInstance(seat, sel, pri, auto, true);
            bool q = ii != null && sel.InteractionQueue.Add(ii);
            sDsOrdered = q ? seat : null;
            if (q) PatchCounters()[4992]++;
            return "doorseat sit: " + sel.FullName + " -> #" + i + " " + seat.GetType().Name + " 0x" + seat.ObjectId.Value.ToString("X") + (auto ? " (autonomous)" : " (ordered)") + ": queued " + q;
        }

        static string DsSeated()
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder("doorseat seated:");
            Household hh = Household.ActiveHousehold;
            if (hh == null) return "doorseat seated: no household";
            bool orderedHit = false;
            foreach (Sim s in hh.Sims)
            {
                Sims3.Gameplay.Interfaces.IGameObject c = s.Posture != null ? s.Posture.Container : null;
                bool sitting = s.Posture is SittingPosture;
                if (sitting && sDsOrdered != null && c == sDsOrdered) orderedHit = true;
                b.Append(" " + s.FirstName + " " + (sitting && c != null ? "sits on " + c.GetType().Name + " 0x" + c.ObjectId.Value.ToString("X") : "stands (" + (s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "idle") + ")") + ";");
            }
            if (sDsOrdered != null)
            {
                PatchCounters()[orderedHit ? 4993 : 4994]++;
                b.Append(" ordered seat 0x" + sDsOrdered.ObjectId.Value.ToString("X") + ": " + (orderedHit ? "SEATED" : "NOT SEATED"));
                sDsOrdered = null;
            }
            return b.ToString();
        }
    }
}
