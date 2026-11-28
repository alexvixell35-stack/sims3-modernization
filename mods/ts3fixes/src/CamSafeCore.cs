using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent part of camsafe (CamSafe.cs, docs/690): one camera-safe "unseen" rule for every off-screen shortcut.
    // Offline selftest: tools/selftest/camsafe.
    //
    // The old tests ask "off screen now, and farther than the camera's reach from its target and eye". The camera can move
    // fast, so a spot that passes that test may be on screen a moment later. camsafe adds three things (after Sunshine-Hill's
    // LOD Trader: what matters is the chance of being seen soon, not being seen now):
    //   1. Predictive margin: the reach is the largest reach of the last HoldS seconds, grown by MarginPct, plus how far the
    //      camera could travel in the next LeadS seconds at its smoothed speed, plus the farthest the camera target or eye has
    //      been from where it is now in the last HoldS seconds. The last term is the "recently seen" hold for spots: a point
    //      within the old reach of any camera pose of the last HoldS seconds is within reach + that distance now (triangle
    //      inequality), so one sphere covers the whole recent camera path.
    //   2. Recently-seen hold for objects and Sims: an object seen on screen in the last HoldS real seconds still counts as
    //      seen. Times are noted only when a caller asks (UcOnScreen), so the cost stays with the Sims that ask.
    //   3. Camera-jump pause: a target, eye or reach change above JumpM between two samples (portrait click, lot jump, map
    //      view, the first sample) holds every spot as seen for JumpHoldS seconds.
    // Times are real milliseconds (Environment.TickCount), so a fast game speed does not shorten the holds.
    public sealed class CamSafeCore
    {
        // tunables (CamSafe.cs copies them from Instantiator before each sample)
        public float MarginPct = 25f, LeadS = 2f, SpeedMax = 150f, SpeedTauMs = 1000f, HoldS = 5f, JumpM = 25f, JumpHoldS = 3f;

        const int kRing = 64, kRingStepMs = 100;  // a pose every 100 ms: 6.4 s of history, more than any sane HoldS
        readonly int[] mMs = new int[kRing];
        readonly float[] mTx = new float[kRing], mTy = new float[kRing], mTz = new float[kRing];
        readonly float[] mPx = new float[kRing], mPy = new float[kRing], mPz = new float[kRing], mR = new float[kRing];
        int mHead, mCount;
        bool mHave;
        int mLastMs, mJumpUntil;
        float mLtx, mLty, mLtz, mLpx, mLpy, mLpz, mLr;

        public float Speed;      // smoothed camera speed, m/s (target or eye, the faster)
        public float Reach;      // the camera-safe reach of the last sample, m (float.MaxValue while a jump holds)
        public int Jumps, Samples;
        readonly Dictionary<ulong, int> mSeenAt = new Dictionary<ulong, int>();

        static float D(float ax, float ay, float az, float bx, float by, float bz)
        {
            float dx = ax - bx, dy = ay - by, dz = az - bz;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public bool Jumping(int nowMs) { return mHave && unchecked(mJumpUntil - nowMs) > 0; }

        // One camera sample: target t, eye p, the old reach. Returns true when this sample is a jump.
        public bool Sample(int nowMs, float tx, float ty, float tz, float px, float py, float pz, float reach)
        {
            Samples++;
            bool jump = false;
            if (!mHave) jump = true;  // the camera found (load, first ask): hold until the samples settle
            else
            {
                float d = Math.Max(D(tx, ty, tz, mLtx, mLty, mLtz), D(px, py, pz, mLpx, mLpy, mLpz));
                if (d > JumpM || Math.Abs(reach - mLr) > JumpM) jump = true;
                int dt = unchecked(nowMs - mLastMs);
                if (dt > 0)
                {
                    float v = Math.Min(SpeedMax, d * 1000f / dt);
                    float decay = (float)Math.Exp(-dt / Math.Max(1f, SpeedTauMs));
                    Speed = Math.Max(v, Speed * decay);
                }
            }
            if (jump) { Jumps++; mJumpUntil = unchecked(nowMs + (int)(JumpHoldS * 1000f)); }
            mHave = true;
            mLastMs = nowMs; mLtx = tx; mLty = ty; mLtz = tz; mLpx = px; mLpy = py; mLpz = pz; mLr = reach;

            // pose history, one every kRingStepMs
            int last = (mHead + kRing - 1) % kRing;
            if (mCount == 0 || unchecked(nowMs - mMs[last]) >= kRingStepMs)
            {
                mMs[mHead] = nowMs; mTx[mHead] = tx; mTy[mHead] = ty; mTz[mHead] = tz; mPx[mHead] = px; mPy[mHead] = py; mPz[mHead] = pz; mR[mHead] = reach;
                mHead = (mHead + 1) % kRing;
                if (mCount < kRing) mCount++;
            }
            int holdMs = (int)(HoldS * 1000f);
            float maxR = reach, disp = 0f;
            for (int i = 0, k = (mHead + kRing - 1) % kRing; i < mCount; i++, k = (k + kRing - 1) % kRing)
            {
                if (unchecked(nowMs - mMs[k]) > holdMs) break;  // newest first: the rest is older
                maxR = Math.Max(maxR, mR[k]);
                disp = Math.Max(disp, Math.Max(D(tx, ty, tz, mTx[k], mTy[k], mTz[k]), D(px, py, pz, mPx[k], mPy[k], mPz[k])));
            }
            Reach = Jumping(nowMs) ? float.MaxValue : maxR * (1f + MarginPct / 100f) + Math.Min(Speed, SpeedMax) * LeadS + disp;
            return jump;
        }

        // Within the camera-safe reach of the camera target or eye (or a jump holds): counts as seen.
        public bool Near(float x, float y, float z)
        {
            if (Reach == float.MaxValue) return true;
            float r2 = Reach * Reach;
            float dx = x - mLtx, dy = y - mLty, dz = z - mLtz;
            if (dx * dx + dy * dy + dz * dz < r2) return true;
            dx = x - mLpx; dy = y - mLpy; dz = z - mLpz;
            return dx * dx + dy * dy + dz * dz < r2;
        }

        // An object was seen on screen now.
        public void NoteSeen(ulong id, int nowMs)
        {
            if (mSeenAt.Count >= 1024) Prune(nowMs);
            mSeenAt[id] = nowMs;
        }

        // Seen on screen in the last HoldS seconds.
        public bool Held(ulong id, int nowMs)
        {
            int at;
            return mSeenAt.TryGetValue(id, out at) && unchecked(nowMs - at) <= (int)(HoldS * 1000f);
        }

        // Drops expired times; if all are fresh (1024 objects seen within HoldS), starts over (the hold is then lost for
        // those, so the old test still applies: never less safe than without camsafe).
        void Prune(int nowMs)
        {
            int holdMs = (int)(HoldS * 1000f);
            List<ulong> old = new List<ulong>();
            foreach (KeyValuePair<ulong, int> kv in mSeenAt) if (unchecked(nowMs - kv.Value) > holdMs) old.Add(kv.Key);
            foreach (ulong id in old) mSeenAt.Remove(id);
            if (mSeenAt.Count >= 1024) mSeenAt.Clear();
        }

        // The verdict of one ask with both rules. onNow: on screen now; held: Held(); farOld: the old distance test passed;
        // nearCs: Near() (or the caller's camera-safe distance test failed). Returns the camsafe verdict (true = unseen).
        public const int kSeenOld = 0, kUnseen = 1, kVetoJump = 2, kVetoHold = 3, kVetoMargin = 4;
        public static int Verdict(bool onNow, bool farOld, bool jumping, bool held, bool nearCs)
        {
            if (onNow || !farOld) return kSeenOld;
            if (jumping) return kVetoJump;
            if (held) return kVetoHold;
            if (nearCs) return kVetoMargin;
            return kUnseen;
        }
    }
}
