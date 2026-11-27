using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/691 alibiwalk / alibiwalklog: a long planned walk of an NPC that nobody can see, off the active lot, is cut to its
    // tail ("alibi generation": the Sim is already where a watcher would expect it). At SimRoutingComponent.DoRoute with a
    // successful plan (the route is about to be followed, so a plan made only to test reachability never moves anyone): a
    // human NPC whose route is >= kAlibiWalkMinM long and made only of walking paths and portals is set at the start of
    // the route's last kAlibiWalkTailM metres (a point of EA's own plan, on its last walking path), the route is replanned
    // from there (EA's push pattern: move, then Route.Replan) and EA walks the tail: doors, slots, arrival callbacks and
    // the interaction after the walk are the game's own. A failed or odd tail replan puts the Sim back where it stood and
    // replans the original walk.
    // Scope: never a household or selected Sim (IsPlayerSim, also with [Experimental] HouseholdToo: a teleport), never a
    // pet, a held / carrying / riding Sim, a Sim with followers or in a GoHereWith group, a route touching the active lot or
    // the active household's lot. Camera: decided once, at the cut, through camsafe's CsDecide (the Sim, its target and
    // both the tail start and the route end unseen; with camsafe off TownieUnseen + FarFromViewers). The cut is one tick, so
    // there is no half state to snap: after it EA's own walk runs. Counters 4920-4939; alibiwalklog alone = compare mode
    // (counts what it would cut). AlibiWalkCheck result | run.
    public static partial class Fixes
    {
        const int kAlwRoutes = 4920, kAlwLong = 4921, kAlwKind = 4922, kAlwScope = 4923, kAlwSeen = 4924, kAlwWould = 4925,
            kAlwCut = 4926, kAlwUndo = 4927, kAlwUndoFail = 4928, kAlwSkippedM = 4929, kAlwErrors = 4930, kAlwTailM = 4931,
            kAlwLastNotWalk = 4932, kAlwLateSeen = 4933, kAlwActiveLot = 4934, kAlwLongM = 4935, kAlwKindWater = 4937, kAlwKindOther = 4938, kAlwUndoLong = 4939, kAlwEndSpot = 4936;
        const int kAlwMaxNames = 48;
        static readonly Dictionary<string, int[]> sAlwBy = new Dictionary<string, int[]>();  // interaction -> long, metres, would/cut, seen
        static string sAlwLast = "", sAlwLastError = "";

        static void AlwError(Exception e)
        {
            sAlwLastError = e.GetType().Name + ": " + e.Message;
            if (PatchCounters()[kAlwErrors]++ < 5) DiagLine("alibiwalk: error " + sAlwLastError + " " + e.StackTrace);
        }

        static void AlwNote(string name, int slot, int metres)
        {
            int[] v;
            if (!sAlwBy.TryGetValue(name, out v))
            {
                if (sAlwBy.Count >= kAlwMaxNames) name = "(other)";
                if (!sAlwBy.TryGetValue(name, out v)) { v = new int[4]; sAlwBy[name] = v; }
            }
            v[0]++; v[1] += metres;
            if (slot >= 2) v[slot]++;
        }

        static bool AlwActiveLot(Lot l) { return l != null && !l.IsWorldLot && (l == LotManager.ActiveLot || l == Household.ActiveHouseholdLot); }

        // Called by BuRouteHook.RouteHook (BlockedUse.cs) before the game follows a successful plan. No catch round the walk:
        // nothing here walks; a reset unwinds through.
        internal static void AlwBefore(Sim sim, Route r)
        {
            bool act = ShortcutOn("alibiwalk");
            if (!act && !ShortcutOn("alibiwalklog")) return;
            int[] c = PatchCounters();
            Vector3 from = Vector3.Invalid, fwd = Vector3.Invalid;
            bool moved = false;
            try
            {
                if (sim == null || sim.HasBeenDestroyed || r == null || !sim.IsHuman || IsPlayerSim(sim) || sim == PlumbBob.SelectedActor) return;
                c[kAlwRoutes]++;
                float len = r.GetRouteLength();
                if (len < Instantiator.kAlibiWalkMinM) return;
                c[kAlwLong]++;
                c[kAlwLongM] += (int)len;
                uint n = r.GetNumPaths();
                if (n == 0 || n > 64) return;
                uint[] types = new uint[n];
                for (uint i = 0; i < n; i++) types[i] = (uint)r.GetPathData(i).PathType;
                float lastLen = r.GetPathLength(n - 1);
                InteractionInstance inst = sim.CurrentInteraction;
                string name = inst != null ? inst.GetType().Name : "(none)";
                int v = AlibiWalkCore.Verdict(len, types, lastLen, Instantiator.kAlibiWalkMinM, Instantiator.kAlibiWalkTailM);
                if (v == AlibiWalkCore.kKind)
                {
                    c[kAlwKind]++;
                    int k = -1;  // car = path kind minus the two below
                    foreach (uint t in types) { if (t == 4027457938u) { k = -1; break; } if (t == 2370016522u || t == 2583738316u || t == 3919275477u) k = kAlwKindWater; else if (!AlibiWalkCore.KindOk(t) && k < 0) k = kAlwKindOther; }
                    if (k >= 0) c[k]++;
                    AlwNote(name, 0, (int)len); return;
                }
                if (v == AlibiWalkCore.kLastNotWalk) { c[kAlwLastNotWalk]++; AlwNote(name, 0, (int)len); return; }
                if (v == AlibiWalkCore.kTailTooLong) { c[kAlwLastNotWalk]++; AlwNote(name, 0, (int)len); return; }
                if (v != AlibiWalkCore.kAct) return;
                // scope: on foot and alone (UfHeld: riding, ridden, in a vehicle, carried, carrying), no followers, no group walk
                List<Sim> fol = inst is Sims3.Gameplay.Abstracts.RabbitHole.IRabbitHoleFollowers ? ((Sims3.Gameplay.Abstracts.RabbitHole.IRabbitHoleFollowers)inst).SimFollowers
                    : inst is GoHome ? ((GoHome)inst).SimFollowers : inst is GoToLot ? ((GoToLot)inst).Followers : null;
                if (UfHeld(sim) || (fol != null && fol.Count > 0) || (inst != null && inst.PartOfGoHereSituation)
                    || sim.Autonomy == null || sim.Autonomy.SituationComponent == null || sim.Autonomy.SituationComponent.InSituationOfType(typeof(Sims3.Gameplay.Objects.Vehicles.GoHereWithSituation)))
                { c[kAlwScope]++; AlwNote(name, 0, (int)len); return; }
                // the tail start: tailM before the end of the last walking path, or that path's start when it is shorter
                Vector3 end = Vector3.Invalid, p = Vector3.Invalid, dir = Vector3.Invalid;
                r.GetPathEndPoint(n - 1, ref end);
                float tail = AlibiWalkCore.TailFrom(lastLen, Instantiator.kAlibiWalkTailM);
                if (tail >= lastLen) { r.GetPathStartPoint(n - 1, ref p); r.GetPathStartDirection(n - 1, ref dir); }
                else
                {
                    // GetSegmentAtDistanceBeforeEnd answers a segment id (EA's route callbacks take ids); the point getters take
                    // an index. bCreate: a segment boundary at that distance (no change to the path, EA does the same for its
                    // lead-in callbacks), in both arms so the log arm counts the same cuts.
                    uint seg = r.GetSegmentAtDistanceBeforeEnd(tail, true), ns = r.GetNumSegments(), idx = uint.MaxValue;
                    for (uint i = ns; i > 0 && ns <= 4096; i--) if (r.GetSegmentIdAtIndex(i - 1) == seg) { idx = i - 1; break; }
                    if (idx == uint.MaxValue || !r.GetSegmentStartPoint(idx, out p) || !r.GetSegmentStartDirection(idx, out dir)) p = Vector3.Invalid;
                }
                // the tail start must be within the tail of the end (a straight line is never longer than the walk along it).
                // A long last path is often one segment (s642b: 123 of 132), so no point of it lies near the end: the Sim is then
                // set on the route's end itself, EA's own goal spot, facing the way it was heading, and the replan is 0 m
                // (EA's arrival runs as for any finished walk).
                if (!BuValid(end)) { c[kAlwLastNotWalk]++; AlwNote(name, 0, (int)len); return; }
                if (!BuValid(p) || (p - end).Length() > tail + 2f)
                {
                    c[kAlwEndSpot]++;
                    p = end; tail = 0f;
                    dir = end - sim.Position;
                }
                if (AlwActiveLot(sim.LotCurrent) || AlwActiveLot(LotManager.GetLotAtPoint(p)) || AlwActiveLot(LotManager.GetLotAtPoint(end)))
                { c[kAlwActiveLot]++; AlwNote(name, 0, (int)len); return; }
                if (!CsDecide(sim, false, r.DestObj as IHasScriptProxy, p, end)) { c[kAlwSeen]++; AlwNote(name, 3, (int)len); return; }
                int skipped = (int)(len - tail);
                if (!act) { c[kAlwWould]++; c[kAlwSkippedM] += skipped; AlwNote(name, 2, (int)len); return; }
                // the cut: EA's push pattern (place, Replan from where the Sim now stands)
                from = sim.Position; fwd = sim.ForwardVector;
                dir.y = 0f;
                if (dir.LengthSqr() < 1e-4f) dir = fwd;
                PlaceSim(sim, p, dir, "alibiwalk " + name);
                moved = true;
                RoutePlanResult res = r.Replan();
                float tl = res.Succeeded() ? r.GetRouteLength() : -1f;
                if (res.Succeeded() && tl <= AlibiWalkCore.TailMax(Instantiator.kAlibiWalkTailM))
                {
                    c[kAlwCut]++; c[kAlwSkippedM] += skipped; c[kAlwTailM] += (int)tl;
                    CsWatch(sim.ObjectId.Value, System.Environment.TickCount, 3);  // camsafe's late-seen audit: on screen within kCamSafeAuditS s counts [4933]
                    AlwNote(name, 2, (int)len);
                    sAlwLast = sim.FullName + " " + name + ": " + (int)len + " m walk cut to " + (int)tl + " m at " + BuAt(p);
                    return;
                }
                // undo: back where it stood, the original walk planned again
                c[kAlwUndo]++;
                if (res.Succeeded()) c[kAlwUndoLong]++;
                if (c[kAlwUndo] <= 20) DiagLine("alibiwalk: undo " + sim.FullName + " " + name + ": " + (int)len + " m route, tail start " + BuAt(p) + " (" + tail.ToString("0.0") + " m before the end " + BuAt(end) + ", last path " + lastLen.ToString("0") + " m of " + n + "), replan " + res.mType + (tl >= 0 ? " " + (int)tl + " m" : ""));
                PlaceSim(sim, from, fwd, "alibiwalk undo " + name);
                moved = false;
                if (!r.Replan().Succeeded()) c[kAlwUndoFail]++;  // the game's DoRoute answers the failed plan as for any replan
                sAlwLast = sim.FullName + " " + name + ": tail replan " + res.mType + " (" + (int)tl + " m), undone";
            }
            catch (ResetException) { throw; }
            catch (Exception e)
            {
                AlwError(e);
                if (moved && from != Vector3.Invalid)
                {
                    try { PlaceSim(sim, from, fwd, "alibiwalk undo"); r.Replan(); } catch (ResetException) { throw; } catch (Exception) { }
                }
            }
        }

        // AlibiWalkCheck: "result" prints the counters and the walks by interaction; "run" evaluates AlibiWalkCore (no game state).
        public static object AlibiWalkCheck(string what)
        {
            try
            {
                int[] c = PatchCounters();
                string sw = "alibiwalk " + (ShortcutOn("alibiwalk") ? "on" : "off") + ", alibiwalklog " + (ShortcutOn("alibiwalklog") ? "on" : "off")
                    + ", camsafe " + (ShortcutOn("camsafe") ? "on" : "off") + ", min " + Instantiator.kAlibiWalkMinM + " m, tail " + Instantiator.kAlibiWalkTailM + " m";
                string counts = "NPC routes followed " + c[kAlwRoutes] + ", long " + c[kAlwLong] + " (" + c[kAlwLongM] + " m), refused: path kind " + c[kAlwKind] + " (car " + (c[kAlwKind] - c[kAlwKindWater] - c[kAlwKindOther]) + ", boat / water " + c[kAlwKindWater] + ", other " + c[kAlwKindOther] + ")"
                    + ", ends in a portal, little to cut or no tail point " + c[kAlwLastNotWalk] + ", scope " + c[kAlwScope] + ", active lot " + c[kAlwActiveLot]
                    + ", seen " + c[kAlwSeen] + "; would cut " + c[kAlwWould] + ", cut " + c[kAlwCut] + " (skipped " + c[kAlwSkippedM] + " m, tails walked " + c[kAlwTailM]
                    + " m, set on the route end " + c[kAlwEndSpot] + ", on screen within the audit window after a cut " + c[kAlwLateSeen] + "), undone " + c[kAlwUndo] + " (undo replan failed " + c[kAlwUndoFail] + "), errors " + c[kAlwErrors];
                if (what == "result")
                {
                    List<KeyValuePair<string, int[]>> l = new List<KeyValuePair<string, int[]>>(sAlwBy);
                    l.Sort(delegate (KeyValuePair<string, int[]> a, KeyValuePair<string, int[]> b) { return b.Value[1].CompareTo(a.Value[1]); });
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int i = 0; i < l.Count && i < 16; i++)
                        sb.Append((i > 0 ? "; " : "") + l[i].Key + " " + l[i].Value[0] + "/" + l[i].Value[1] + "m cut " + l[i].Value[2] + " seen " + l[i].Value[3]);
                    return "alibiwalk result (" + sw + "): " + counts + "; long walks by interaction (n/m, cut or would, seen): " + sb
                        + (sAlwLast.Length > 0 ? "; last " + sAlwLast : "") + (sAlwLastError.Length > 0 ? "; error " + sAlwLastError : "");
                }
                uint[] walk = { AlibiWalkCore.kSimPath, AlibiWalkCore.kPortalPath, AlibiWalkCore.kSimPath };
                uint[] car = { AlibiWalkCore.kSimPath, 4027457938u, AlibiWalkCore.kSimPath };
                bool ok = AlibiWalkCore.Verdict(200f, walk, 30f, 60f, 10f) == AlibiWalkCore.kAct
                    && AlibiWalkCore.Verdict(50f, walk, 30f, 60f, 10f) == AlibiWalkCore.kShort
                    && AlibiWalkCore.Verdict(200f, car, 30f, 60f, 10f) == AlibiWalkCore.kKind
                    && AlibiWalkCore.Verdict(200f, new uint[] { AlibiWalkCore.kSimPath, AlibiWalkCore.kPortalPath }, 2f, 60f, 10f) == AlibiWalkCore.kLastNotWalk
                    && AlibiWalkCore.TailFrom(4f, 10f) == 4f && AlibiWalkCore.TailFrom(40f, 10f) == 10f;
                return "alibiwalk check (" + sw + "): rules " + (ok ? "ok" : "BAD") + "; " + counts;
            }
            catch (Exception e) { return "AlibiWalkCheck: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
