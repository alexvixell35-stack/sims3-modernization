using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // photosession (docs/508, core Builds 16 + 97): a greeting-card photo session (the Seasons festival photo booth, or City
    // Hall and the other greeting-card rabbit holes) lists the Sims it expects in GreetingCardTakePhotoHelper.mSimsExpected.
    // Only the SimDoneWithPhoto call that empties the list ends the session (Reset, and the caller clears the owner's
    // mPhotoHelper; the booth is InUse while that is set). A Sim can stay on the list for good:
    //   a. GetPhotoTakenWith.Run lists every picked Sim, also one whose TakePhoto never got queued, and a Sim reset or deleted
    //      while queued never calls SimDoneWithPhoto;
    //   b. SimDoneWithPhoto removes a Sim only while the session waits for Sims or is done, not one that leaves while the
    //      others are ready (a reset, the loop's timeout).
    // The Sims inside then never become ready, give up after the loop's timeout, leave, and the booth stays unavailable,
    // after a reload too (the helper is saved with the owner) [inferred: no Persistable(false) on the field].
    // Fix, switch on (whole-method wraps; EA's code runs as it is):
    //   SimDoneWithPhoto: after EA's call, if the session did not end and is not taking the photo, the Sim that called is
    //   dropped too and so is every expected Sim with no interaction on the owner (PhotoSessionCore.Drop: a toddler or baby
    //   stays while an older Sim of the session stays). If the list is empty then, EA's own Reset ends the session and the
    //   call returns true, as EA's does when its own removal empties the list.
    //   SimMadeItInside: the same drop before EA's call (never down to an empty list), so EA's "inside == expected" can
    //   become true.
    //   World load: a session on a booth or rabbit hole none of whose expected Sims has an interaction on it is ended.
    // Not ported (needs a mid-method change, docs/506): Build 16's ">= expected" checks and the bounded camera wait.
    // [2026] expected Sims dropped, [2027] sessions ended by the hook, [2028] sessions ended at world load.
    public static partial class Fixes
    {
        public class PhotoSessionHook
        {
            public bool SimDoneHook(SimDescription simDesc)
            {
                bool r = SimDoneOrig(simDesc);
                if (r || !ShortcutOn("photosession")) return r;
                try { return PhotoSessionAfterDone((GreetingCardTakePhotoHelper)(object)this, simDesc); }
                catch (Exception e) { PhotoSessionError("SimDoneWithPhoto", e); return false; }
            }
            public void MadeInsideHook()
            {
                if (ShortcutOn("photosession"))
                {
                    try { PhotoSessionPrune((GreetingCardTakePhotoHelper)(object)this, true); }
                    catch (Exception e) { PhotoSessionError("SimMadeItInside", e); }
                }
                MadeInsideOrig();
            }
            public bool SimDoneOrig(SimDescription simDesc) { return false; }  // receives SimDoneWithPhoto's original header
            public void MadeInsideOrig() { }                                    // receives SimMadeItInside's original header
        }

        const BindingFlags kPsInst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const int kPsTakingPhoto = 2;  // GreetingCardTakePhotoHelper.State.TakingPhoto
        static readonly Type sPsType = typeof(GreetingCardTakePhotoHelper);
        static readonly FieldInfo sPsState = sPsType.GetField("mCurrentState", kPsInst), sPsExpected = sPsType.GetField("mSimsExpected", kPsInst),
            sPsOwner = sPsType.GetField("mOwner", kPsInst);
        static readonly MethodInfo sPsReset = sPsType.GetMethod("Reset", kPsInst, null, Type.EmptyTypes, null);
        static readonly FieldInfo sPsBoothHelper = typeof(GreetingCardPhotoBooth).GetField("mPhotoHelper", kPsInst),
            sPsRabbitHoleHelper = typeof(GreetingCardRabbitHole).GetField("mPhotoHelper", kPsInst);
        static int sPsLines;

        static List<SimDescription> PsExpected(GreetingCardTakePhotoHelper h) { return sPsExpected != null ? sPsExpected.GetValue(h) as List<SimDescription> : null; }
        static int PsState(GreetingCardTakePhotoHelper h) { return sPsState != null ? Convert.ToInt32(sPsState.GetValue(h)) : -1; }
        static void PsLog(string s) { if (sPsLines++ < 10) ShortcutLog("photosession: " + s); }
        static void PhotoSessionError(string where, Exception e) { PsLog(where + " hook: " + e.GetType().Name + ": " + e.Message); }

        static bool PsHasInteractionOn(Sim s, IGameObject owner)
        {
            if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null || owner == null) return false;
            foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
                if (ii != null && ii.Target == owner) return true;
            return false;
        }

        static int PhotoSessionPrune(GreetingCardTakePhotoHelper h, bool keepOne)
        {
            List<SimDescription> l = PsExpected(h);
            IGameObject owner = sPsOwner != null ? sPsOwner.GetValue(h) as IGameObject : null;
            if (l == null || l.Count == 0 || owner == null || PsState(h) == kPsTakingPhoto) return 0;
            bool[] act = new bool[l.Count], young = new bool[l.Count];
            for (int i = 0; i < l.Count; i++)
            {
                act[i] = l[i] != null && PsHasInteractionOn(l[i].CreatedSim, owner);
                young[i] = l[i] != null && l[i].ToddlerOrBelow;
            }
            bool[] drop = PhotoSessionCore.Drop(act, young, keepOne);
            int n = 0;
            for (int i = l.Count - 1; i >= 0; i--)
            {
                if (!drop[i]) continue;
                PsLog("dropped " + (l[i] != null ? l[i].FullName : "a null entry") + " (no interaction on " + owner.GetType().Name + ")");
                l.RemoveAt(i); n++;
            }
            PatchCounters()[2026] += n;
            return n;
        }

        static bool PhotoSessionAfterDone(GreetingCardTakePhotoHelper h, SimDescription simDesc)
        {
            List<SimDescription> l = PsExpected(h);
            if (l == null || sPsReset == null || PsState(h) == kPsTakingPhoto) return false;
            if (simDesc != null && l.Remove(simDesc)) PatchCounters()[2026]++;  // EA keeps a Sim that leaves while the others are ready
            PhotoSessionPrune(h, false);
            if (l.Count > 0) return false;
            sPsReset.Invoke(h, null);
            PatchCounters()[2027]++;
            PsLog("session ended (" + (simDesc != null ? simDesc.FullName : "?") + " was the last Sim)");
            return true;
        }

        // World load (Instantiator): end the sessions none of whose expected Sims is still at it.
        public static void PhotoSessionLoadSweep()
        {
            if (!ShortcutOn("photosession")) return;
            int n = 0;
            if (sPsBoothHelper != null) foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GreetingCardPhotoBooth>()) n += PsClearStale(o, sPsBoothHelper);
            if (sPsRabbitHoleHelper != null) foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GreetingCardRabbitHole>()) n += PsClearStale(o, sPsRabbitHoleHelper);
            if (n > 0) PsLog("world load: " + n + " stale session(s) ended");
        }
        static int PsClearStale(GameObject o, FieldInfo f)
        {
            GreetingCardTakePhotoHelper h = o != null ? f.GetValue(o) as GreetingCardTakePhotoHelper : null;
            if (h == null) return 0;
            List<SimDescription> l = PsExpected(h);
            if (l != null) foreach (SimDescription d in l) if (d != null && PsHasInteractionOn(d.CreatedSim, o)) return 0;
            if (sPsReset != null) sPsReset.Invoke(h, null);
            f.SetValue(o, null);
            PatchCounters()[2028]++;
            return 1;
        }

        // Repro check (tools/harness/repros.json photo-session-check), on the first greeting-card rabbit hole with no session
        // and two Sims older than toddlers with no interaction on it. Three cases, each a new session of both Sims:
        //   1. waiting for Sims: the first Sim is done; 2. ready for the photo (state set): the first Sim is done;
        //   3. the session put on the rabbit hole, then the world-load sweep.
        // "broken" (switch off) throws unless EA's outcome: 1 returns false with the second Sim still listed, 2 returns false
        // with both listed, 3 leaves the session on the rabbit hole. "fixed" (switch on) throws unless 1 and 2 return true with
        // the list cleared (Reset), 3 clears the rabbit hole's field, and [2026] +3, [2027] +2, [2028] +1. The rabbit hole's field
        // is put back to null in both arms.
        public static string PhotoSessionCheck(string what)
        {
            GreetingCardRabbitHole[] rhs = Sims3.Gameplay.Queries.GetObjects<GreetingCardRabbitHole>();
            int c26 = PatchCounters()[2026], c27 = PatchCounters()[2027], c28 = PatchCounters()[2028];
            string state = "switch " + ShortcutOn("photosession") + ", " + rhs.Length + " greeting-card rabbit holes, "
                + Sims3.Gameplay.Queries.GetObjects<GreetingCardPhotoBooth>().Length + " booths, counters " + c26 + "/" + c27 + "/" + c28;
            if (what == "state") return state;
            if (what != "broken" && what != "fixed") return "unknown mode " + what + " (state, broken, fixed)";
            if (sPsState == null || sPsExpected == null || sPsOwner == null || sPsReset == null || sPsRabbitHoleHelper == null)
                throw new InvalidOperationException("photosession: a GreetingCardTakePhotoHelper member was not found; " + state);
            GreetingCardRabbitHole rh = null;
            foreach (GreetingCardRabbitHole r in rhs) if (r != null && !r.HasBeenDestroyed && sPsRabbitHoleHelper.GetValue(r) == null) { rh = r; break; }
            if (rh == null) throw new InvalidOperationException("photosession " + what + ": no greeting-card rabbit hole without a session; " + state);
            List<SimDescription> two = new List<SimDescription>();
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                if (two.Count < 2 && s != null && !s.HasBeenDestroyed && s.SimDescription != null && !s.SimDescription.ToddlerOrBelow && !PsHasInteractionOn(s, rh))
                    two.Add(s.SimDescription);
            if (two.Count < 2) throw new InvalidOperationException("photosession " + what + ": fewer than two Sims to list; " + state);
            bool on = what == "fixed";
            List<GreetingCardTakePhotoHelper.AdultBabyPair> pairs = new List<GreetingCardTakePhotoHelper.AdultBabyPair>();

            GreetingCardTakePhotoHelper h1 = new GreetingCardTakePhotoHelper(rh, new List<SimDescription>(two), pairs);
            bool r1 = h1.SimDoneWithPhoto(two[0]);
            int n1 = PsExpected(h1) != null ? PsExpected(h1).Count : -1;
            GreetingCardTakePhotoHelper h2 = new GreetingCardTakePhotoHelper(rh, new List<SimDescription>(two), pairs);
            sPsState.SetValue(h2, Enum.ToObject(sPsState.FieldType, 1));  // ReadyForPhoto
            bool r2 = h2.SimDoneWithPhoto(two[0]);
            int n2 = PsExpected(h2) != null ? PsExpected(h2).Count : -1;
            GreetingCardTakePhotoHelper h3 = new GreetingCardTakePhotoHelper(rh, new List<SimDescription>(two), pairs);
            sPsRabbitHoleHelper.SetValue(rh, h3);
            PhotoSessionLoadSweep();
            bool kept = sPsRabbitHoleHelper.GetValue(rh) != null;
            sPsRabbitHoleHelper.SetValue(rh, null);

            string res = "on " + rh.GetType().Name + " with " + two[0].FullName + " and " + two[1].FullName + ": 1 returned " + r1 + " (" + n1
                + " listed), 2 returned " + r2 + " (" + n2 + " listed), 3 session " + (kept ? "kept" : "ended") + "; counters +"
                + (PatchCounters()[2026] - c26) + "/+" + (PatchCounters()[2027] - c27) + "/+" + (PatchCounters()[2028] - c28);
            bool ok = on
                ? r1 && n1 == -1 && r2 && n2 == -1 && !kept && PatchCounters()[2026] - c26 == 3 && PatchCounters()[2027] - c27 == 2 && PatchCounters()[2028] - c28 == 1
                : !r1 && n1 == 1 && !r2 && n2 == 2 && kept;
            if (!ok) throw new InvalidOperationException("photosession " + what + ": wrong outcome: " + res + "; " + state);
            return res;
        }
    }
}
