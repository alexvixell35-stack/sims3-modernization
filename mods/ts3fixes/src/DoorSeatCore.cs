namespace Ts3Fixes
{
    // Game-independent rules of DoorSeat.cs (doorseat / doorseathh, docs/686 §39), checked offline by tools/selftest/doorseat.
    //
    // A dining chair (or bar stool) next to a door or arch: before the Sim sits, the game tests whether the chair may pull
    // out (CanScootBack, a chair at a table) or whether its feet area is free (CanEnableFeetFootprint, a chair on its own)
    // with World.TestObjectPlacement, the build-mode placement test. That test also refuses the door's keep-clear area,
    // which only keeps furniture from being placed in a doorway; a Sim may sit there. The rules here say which refused
    // tests are retried with the door allowed, and for whom.
    public static class DoorSeatCore
    {
        public const int kNone = 0, kWould = 1, kExempt = 2, kGo = 3;

        // A refused seat test is a case when the game refused it for a Sim about to sit (the build-mode and load-time calls
        // pass no Sim and keep the game's answer) and a door or arch stands within reach of the chair.
        public static bool Kind(bool eaResult, bool hasSim, int doorsNear)
        {
            return !eaResult && hasSim && doorsNear > 0;
        }

        // Scope: doorseat covers Sims the player does not control, doorseathh household and selected Sims; each stands alone.
        // With both off every case counts as "would". Humans only (a pet never sits on a dining chair).
        public static int Decide(bool kind, bool human, bool player, bool on, bool hhOn)
        {
            if (!kind) return kNone;
            if (!human) return kExempt;
            if (!on && !hhOn) return kWould;
            if (player) return hhOn ? kGo : kExempt;
            return on ? kGo : kWould;
        }

        // Where a point lies against a door: f along the door's forward vector (through the opening), s along its side
        // (along the wall), both in metres from the door's centre. (dx, dz) = point - door, (fx, fz) = the door's forward.
        public static void DoorLocal(float dx, float dz, float fx, float fz, out float f, out float s)
        {
            float n = (float)System.Math.Sqrt(fx * fx + fz * fz);
            if (n < 1e-4f) { f = 0f; s = 0f; return; }
            fx /= n; fz /= n;
            f = dx * fx + dz * fz;
            s = dx * -fz + dz * fx;
        }

        // The door's keep-clear area as the probe reports it: in front of or behind the opening (|f| <= depth) and no
        // farther along the wall than half the door's width plus margin. A report label, not a rule of the fix.
        public static bool InClearArea(float f, float s, float halfWidth, float depth)
        {
            return System.Math.Abs(f) <= depth && System.Math.Abs(s) <= halfWidth;
        }

        // The doors the retry may allow: within reach of the chair (flat distance) and on its level.
        public static bool DoorNear(float flat, float dy, float reach)
        {
            return flat <= reach && System.Math.Abs(dy) <= 1f;
        }

        // Retry outcome: the first door that alone makes the test pass is the one allowed. None = the game's answer stays
        // (a wall, another object or a Sim refuses the chair too).
        public static int FirstPass(bool[] passWithDoor, int n)
        {
            for (int i = 0; i < n && i < passWithDoor.Length; i++) if (passWithDoor[i]) return i;
            return -1;
        }
    }
}
