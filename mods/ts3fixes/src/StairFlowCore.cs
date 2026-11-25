using System;

namespace Ts3Fixes
{
    // Game-independent rules of stairflow / stairtimelog (StairFlow.cs, docs/686 §32). Offline selftest: tools/selftest/stairflow.
    public static class StairFlowCore
    {
        public const int kNo = 0, kGo = 1, kWould = 2, kExempt = 3, kKept = 4;

        // Angle in degrees (0-180) between two directions in plan view (y ignored). 180 when either is (near) zero.
        public static float AngleDeg(float ax, float az, float bx, float bz)
        {
            double la = Math.Sqrt(ax * ax + az * az), lb = Math.Sqrt(bx * bx + bz * bz);
            if (la < 1e-4 || lb < 1e-4) return 180f;
            double c = (ax * bx + az * bz) / (la * lb);
            if (c > 1) c = 1; else if (c < -1) c = -1;
            return (float)(Math.Acos(c) * 180.0 / Math.PI);
        }

        // Drop the stop animation before the stairs (approach) or the start animation after them (exit)?
        // Only straight stairs, only when the walk already heads along the lane (angle <= maxDeg), so the stair walk does not
        // have to turn the Sim: with a larger angle the game's stop-and-face stays (kKept).
        public static int Decide(bool on, bool inScope, bool straightStairs, float angleDeg, float maxDeg)
        {
            if (!straightStairs) return kNo;
            if (angleDeg > maxDeg) return kKept;
            if (!on) return kWould;
            if (!inScope) return kExempt;
            return kGo;
        }
    }
}
