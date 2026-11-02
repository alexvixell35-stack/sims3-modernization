using System;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // eggspot (docs/508, core Build 73): EggHuntController.PlaceEgg picks random start points within half the lot's size
    // around EggHuntLot.Position and lets GlobalFunctions.PlaceAtGoodLocation move the egg, with IsValidEggPosition as the
    // check. That check asks only for an outdoor, routable point, never for a point on EggHuntLot, so some eggs land on the
    // world or a neighbour lot. An egg off the lot stays in the controller's mPlacedEggs, but its lot has no
    // CurrEggHuntController, so picking it up throws in EasterEgg.PickUpEgg.Run (core Build 72, not portable: mid-method)
    // and the hunt never sees its last egg go.
    // Fix, switch on (whole-method wrap): after EA's check says yes, a point whose LotManager.GetLotAtPoint is not EggHuntLot
    // is refused. PlaceEgg keeps its tries and the good-location search keeps looking near the start point. A controller with
    // no EggHuntLot gets EA's answer.
    // [2029] egg positions refused because they are off the hunt lot.
    public static partial class Fixes
    {
        public class EggSpotHook
        {
            public bool ValidHook(float posX, float posY, float posZ, float absAngle)
            {
                bool r = ValidOrig(posX, posY, posZ, absAngle);
                if (!r || !ShortcutOn("eggspot")) return r;
                try { return EggSpotOnLot((EggHuntController)(object)this, new Vector3(posX, posY, posZ)); }
                catch (Exception e) { EggSpotLog("hook: " + e.GetType().Name + ": " + e.Message); return r; }
            }
            public bool ValidOrig(float posX, float posY, float posZ, float absAngle) { return false; }  // receives IsValidEggPosition's original header
        }
        static int sEggLines;
        static void EggSpotLog(string s) { if (sEggLines++ < 5) ShortcutLog("eggspot: " + s); }
        // The check calls the swapped IsValidEggPosition through a delegate: MethodInfo.Invoke fails on a swapped method
        // ("Unable to invoke an invalid target", EaFixes.cs PrefRemoveFn).
        delegate bool EggValidFn(float posX, float posY, float posZ, float absAngle);

        static bool EggSpotOnLot(EggHuntController c, Vector3 p)
        {
            Lot hunt = c.EggHuntLot;
            if (hunt == null || LotManager.GetLotAtPoint(p) == hunt) return true;
            PatchCounters()[2029]++;
            EggSpotLog("refused a point off " + hunt.Name);
            return false;
        }

        // Repro check (tools/harness/repros.json egg-spot-check). Needs no egg hunt: a controller object (not constructed, no
        // hunt state) gets a hunt lot, and EA's check runs on the position of an outdoor Sim on lot M, first with EggHuntLot = M
        // (the precondition: EA's check says yes there), then with EggHuntLot = another lot. "broken" (switch off) throws unless
        // the point off the hunt lot is accepted (EA's gap). "fixed" (switch on) throws unless it is refused, the on-lot call
        // still says yes, and [2029] went up by 1.
        public static string EggSpotCheck(string what)
        {
            string state = "switch " + ShortcutOn("eggspot") + ", refused so far " + PatchCounters()[2029];
            if (what == "state") return state;
            if (what != "broken" && what != "fixed") return "unknown mode " + what + " (state, broken, fixed)";
            MethodInfo valid = typeof(EggHuntController).GetMethod("IsValidEggPosition", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (valid == null) throw new InvalidOperationException("eggspot: EggHuntController.IsValidEggPosition not found");
            EggHuntController c = (EggHuntController)FormatterServices.GetUninitializedObject(typeof(EggHuntController));
            EggValidFn check = (EggValidFn)Delegate.CreateDelegate(typeof(EggValidFn), c, valid);
            int c0 = PatchCounters()[2029];
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (s == null || s.HasBeenDestroyed) continue;
                Vector3 p = s.Position;
                Lot m = LotManager.GetLotAtPoint(p);
                if (m == null || !World.IsPositionOutside(p)) continue;
                Lot other = null;
                foreach (Lot l in LotManager.AllLots) if (l != null && l != m) { other = l; break; }
                if (other == null) continue;

                c.EggHuntLot = m;
                if (!check(p.x, p.y, p.z, 0f)) continue;  // EA's own check says no here: try another Sim
                c.EggHuntLot = other;
                bool off = check(p.x, p.y, p.z, 0f);
                c.EggHuntLot = m;
                bool onLot = check(p.x, p.y, p.z, 0f);
                string res = "point of " + s.FullName + " on " + m.Name + ": hunt lot " + other.Name + " -> " + off + ", hunt lot " + m.Name
                    + " -> " + onLot + ", refused +" + (PatchCounters()[2029] - c0);
                bool ok = what == "fixed" ? !off && onLot && PatchCounters()[2029] - c0 == 1 : off && onLot;
                if (!ok) throw new InvalidOperationException("eggspot " + what + ": wrong outcome: " + res + "; " + state);
                return res;
            }
            throw new InvalidOperationException("eggspot " + what + ": no outdoor Sim whose position EA's check accepts; " + state);
        }
    }
}
