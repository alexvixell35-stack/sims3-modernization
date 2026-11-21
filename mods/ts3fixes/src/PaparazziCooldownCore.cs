using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of paparazzicooldown (PaparazziCooldown.cs, docs/648). Self-check: tools/selftest/paparazzicooldown.
    // (a) Route-check stretch: EA calls FollowCelebrity's loop callback every kPaparazziRouteCheckTime (1 sim-minute) of the
    // loop's lifetime. Per follow (paparazzo, instance token) the lifetime of the last callback that ran EA's body is kept. A
    // callback is skipped when the follow is eligible (both Sims unseen, non-exempt), EA's exit is not due (follow time over or
    // the celebrity left the lot: those always run EA's body, which ends the follow), and less than StretchMinutes of lifetime
    // have passed since that last run. A new instance token starts over; a lifetime that went back runs EA's body.
    // (b) Re-follow cooldown: when a follow ends, (paparazzo, celebrity) is kept with its sim time; a new follow of the same
    // pair within CooldownTicks is refused. A clock that went back drops the entry. Tables are bounded (kMax): past it the
    // stale entries go, then all.
    public sealed class PaparazziCooldownCore
    {
        public const int kMax = 256;
        public const int kRun = 0, kSkip = 1;

        sealed class Follow { public int Token; public float Last; public bool Stretched; }

        struct Pair : IEquatable<Pair>
        {
            public readonly ulong Pap, Celeb;
            public Pair(ulong pap, ulong celeb) { Pap = pap; Celeb = celeb; }
            public bool Equals(Pair o) { return Pap == o.Pap && Celeb == o.Celeb; }
            public override bool Equals(object o) { return o is Pair && Equals((Pair)o); }
            public override int GetHashCode() { return unchecked((int)Pap ^ (int)(Pap >> 32) * 31 ^ (int)Celeb * 17 ^ (int)(Celeb >> 32)); }
        }

        readonly Dictionary<ulong, Follow> mFollows = new Dictionary<ulong, Follow>();
        readonly Dictionary<Pair, long> mEnded = new Dictionary<Pair, long>();

        public int EndedCount { get { return mEnded.Count; } }

        // pure: skip this callback?
        public static bool Skip(bool eligible, bool exitDue, bool hasLast, float life, float last, float stretchMinutes)
        {
            if (!eligible || exitDue || !hasLast || stretchMinutes <= 0f) return false;
            return life >= last && life - last < stretchMinutes;
        }

        // one loop callback; firstStretch is true on the first skip of this follow (for the log)
        public int Callback(ulong pap, int token, float life, bool eligible, bool exitDue, float stretchMinutes, out bool firstStretch)
        {
            firstStretch = false;
            Follow f;
            bool has = mFollows.TryGetValue(pap, out f) && f.Token == token;
            if (has && Skip(eligible, exitDue, true, life, f.Last, stretchMinutes))
            {
                if (!f.Stretched) { f.Stretched = true; firstStretch = true; }
                return kSkip;
            }
            if (!has)
            {
                if (f == null && mFollows.Count >= kMax) mFollows.Clear();  // one entry per live paparazzo; a flood is a leak
                f = new Follow();
                f.Token = token;
                mFollows[pap] = f;
            }
            f.Last = life;
            return kRun;
        }

        public void Ended(ulong pap, ulong celeb, long now)
        {
            mFollows.Remove(pap);
            if (pap == 0 || celeb == 0) return;
            Pair k = new Pair(pap, celeb);
            if (mEnded.Count >= kMax && !mEnded.ContainsKey(k)) Prune(now);
            mEnded[k] = now;
        }

        // true: refuse this follow (the same pair ended less than cooldownTicks ago)
        public bool Cooling(ulong pap, ulong celeb, long now, long cooldownTicks)
        {
            Pair k = new Pair(pap, celeb);
            long at;
            if (cooldownTicks <= 0 || !mEnded.TryGetValue(k, out at)) return false;
            if (now >= at && now - at < cooldownTicks) return true;
            mEnded.Remove(k);
            return false;
        }

        // the cooldown time this pair has left (0 = none), for the log
        public long Left(ulong pap, ulong celeb, long now, long cooldownTicks)
        {
            long at;
            if (!mEnded.TryGetValue(new Pair(pap, celeb), out at) || now < at) return 0;
            long left = cooldownTicks - (now - at);
            return left > 0 ? left : 0;
        }

        void Prune(long now)
        {
            // the oldest half goes (entries older than any cooldown are among them)
            List<KeyValuePair<Pair, long>> all = new List<KeyValuePair<Pair, long>>(mEnded);
            all.Sort(delegate (KeyValuePair<Pair, long> a, KeyValuePair<Pair, long> b) { return a.Value.CompareTo(b.Value); });
            for (int i = 0; i < all.Count / 2; i++) mEnded.Remove(all[i].Key);
            foreach (KeyValuePair<Pair, long> kv in all) if (kv.Value > now) mEnded.Remove(kv.Key);
        }

        public void Clear() { mFollows.Clear(); mEnded.Clear(); }
    }
}
