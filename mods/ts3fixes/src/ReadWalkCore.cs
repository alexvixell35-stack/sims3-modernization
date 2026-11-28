namespace Ts3Fixes
{
    // Game-independent rules of readwalk (ReadWalk.cs, docs/686 §22). Offline self-check: tools/selftest/readwalk.
    public static class ReadWalkCore
    {
        public const int kWalk = 0, kWaitPart = 1, kKeepNoSlot = 2, kKeepOnRoute = 3, kKeepChildIn = 4;
        public const float kClear = 0.7f;  // the sit slot must be this far from the child's way to its entry slot (m)

        // Squared distance (x/z) from point p to the segment a-b.
        public static float SegDistSq(float px, float pz, float ax, float az, float bx, float bz)
        {
            float dx = bx - ax, dz = bz - az, l = dx * dx + dz * dz, t = 0f;
            if (l > 1e-6f) { t = ((px - ax) * dx + (pz - az) * dz) / l; if (t < 0f) t = 0f; else if (t > 1f) t = 1f; }
            float qx = ax + t * dx - px, qz = az + t * dz - pz;
            return qx * qx + qz * qz;
        }

        // `childIn`: the child's BeReadToSleep runs (EA walks the parent now itself). `partKnown`: the child's bed part is
        // known (claimed, its entry part, or the bed's only part). `slotOk`: the part has a sit slot. `clearSq`: squared
        // distance from the sit slot to the child's way (its position to its entry slot).
        public static int Step(bool childIn, bool partKnown, bool slotOk, float clearSq)
        {
            if (childIn) return kKeepChildIn;
            if (!partKnown) return kWaitPart;
            if (!slotOk) return kKeepNoSlot;
            if (clearSq < kClear * kClear) return kKeepOnRoute;
            return kWalk;
        }
    }
}
