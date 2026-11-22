namespace Ts3Fixes
{
    // Game-independent log cap: at most perWindow lines in each
    // window of windowMs (Environment.TickCount), plus a lifetime ceiling so a loop cannot fill the log. Take() is true
    // when a line may be written. A tick count that wraps or goes back starts a new window.
    public sealed class LineCapCore
    {
        readonly int mPerWindow, mWindowMs, mLifetime;
        int mWindowStart, mInWindow, mTotal;
        bool mStarted;

        public LineCapCore(int perWindow, int windowMs, int lifetime)
        {
            mPerWindow = perWindow; mWindowMs = windowMs; mLifetime = lifetime;
        }

        public int Total { get { return mTotal; } }

        public bool Take(int now)
        {
            if (mTotal >= mLifetime) return false;
            if (!mStarted || now - mWindowStart >= mWindowMs || now - mWindowStart < 0)
            {
                mStarted = true; mWindowStart = now; mInWindow = 0;
            }
            if (mInWindow >= mPerWindow) return false;
            mInWindow++; mTotal++;
            return true;
        }
    }
}
