using System;

namespace Ts3Fixes
{
    // docs/124 §16-§17 go-live consume, the part with no game types (selftest: node tools/selftest/autlive/run.ts):
    //  - Heap: the game's BestScores queue as the meta phase uses it (BestScores.Add with AllowZeroScore off: scores > 0 only;
    //    PriorityQueue capacity 10: add while not full, else replace the root minimum only when strictly greater; slot order
    //    and tie rules as the game's heap) [verified, decompiled BestScores.Add / PriorityQueue]. AlwaysChooseBest pairs never
    //    reach the queue (BestScores keeps them on a side list): callers leave them out. Used by the dry compare (G7) to replay
    //    the game's own C1 calls.
    //  - Order: the worker's passing pairs by worker score, highest first, ties in request order (B13 object order, then
    //    MetaAds order).
    //  - Bands: the §16.1 rule. Band 1 = the first TopN of Order (sent at B14). Band k+1 fires when (a) fewer than 10 entries
    //    and an unsent passing pair remains, or (b) w x (1 + drift + margin) >= s10 (w = best unsent worker score, s10 = the
    //    lowest live entry, drift = largest relative drop worker -> live over the pairs sent so far, a live fail = 1). After
    //    Bands - 1 extra bands every remaining pair goes (full).
    public static class AutLiveCore
    {
        public const int kCap = 10;

        // Per-search feature mask (Rodney 2026-10-31: a wrong pick or crash names the feature without bisecting). One bit per
        // feature that touched this search. Native bits come from Config(33) (ts3native AS_FEAT_*: 1 PreLearn rows at the cut,
        // 2 TierReqLots refresh at the cut, 4 Satb redirected loads in the job, 8 cold give-up in the job).
        public static class Feat
        {
            public const int Late = 0, PreLearn = 1, TierReqLots = 2, Rain = 3, DesireSkip = 4, Satb = 5, Guard = 6, Cold = 7, Partial = 8, N = 9;
            public static readonly string[] Names = { "late", "prelearn", "tierreqlots", "rain", "desirefn", "satb", "guard", "cold", "partial" };
            // at the finish: native = Config(33); rain = pairs answered from the cut's shelter bit; mode3 = the desirefn skip ran
            // (Mode 3 never scores the driver's meta); guard = posted in the cost guard's probation window; partial = KeepPartial
            public static int Build(int native, int rainPairs, bool mode3, bool guardProbation, bool partial)
            {
                int m = 0;
                if ((native & 1) != 0) m |= 1 << PreLearn;
                if ((native & 2) != 0) m |= 1 << TierReqLots;
                if ((native & 4) != 0) m |= 1 << Satb;
                if ((native & 8) != 0) m |= 1 << Cold;
                if (rainPairs > 0) m |= 1 << Rain;
                if (mode3) m |= 1 << DesireSkip;
                if (guardProbation) m |= 1 << Guard;
                if (partial) m |= 1 << Partial;
                return m;
            }
            public static string Text(int mask)
            {
                string t = "";
                for (int b = 0; b < N; b++) if ((mask & (1 << b)) != 0) t += (t.Length > 0 ? "|" : "") + Names[b];
                return t;
            }
            // JSON fields for a record: ,"feat":"0x109","feats":"late|rain|partial"
            public static string Json(int mask) { return ",\"feat\":\"0x" + mask.ToString("X3") + "\",\"feats\":\"" + Text(mask) + "\""; }
            // one count per set bit into c[slot + bit] (counter blocks of N slots)
            public static void Count(int[] c, int slot, int mask)
            {
                for (int b = 0; b < N; b++) if ((mask & (1 << b)) != 0 && slot + b < c.Length) c[slot + b]++;
            }
        }

        public sealed class Heap
        {
            public readonly int[] Id;
            public readonly float[] S;
            public int N;
            readonly int mCap;
            public Heap(int cap) { mCap = cap; Id = new int[cap]; S = new float[cap]; }
            public void Clear() { N = 0; }
            // the game's PriorityQueue in the same slot order: a binary min-heap [verified, decompiled PriorityQueue +
            // InteractionCandidate.CompareTo]: insert sifts up only while strictly smaller than the parent; full = replace the
            // root only when strictly greater, then sift down to the smaller child (left only when strictly smaller than right),
            // swapping only while that child is strictly smaller. Which of several equal minimums goes depends on this order.
            public void Add(int id, float s)
            {
                if (!(s > 0f)) return;
                int i;
                if (N < mCap)
                {
                    i = N++;
                    Id[i] = id; S[i] = s;
                    while (i > 0) { int p = (i - 1) >> 1; if (!(S[i] < S[p])) break; Swap(i, p); i = p; }
                    return;
                }
                if (!(s > S[0])) return;
                Id[0] = id; S[0] = s;
                i = 0;
                while (2 * i + 1 < N)
                {
                    int c = 2 * i + 1;
                    if (c + 1 < N && !(S[c] < S[c + 1])) c++;
                    if (!(S[c] < S[i])) break;
                    Swap(i, c); i = c;
                }
            }
            void Swap(int a, int b) { int t = Id[a]; Id[a] = Id[b]; Id[b] = t; float u = S[a]; S[a] = S[b]; S[b] = u; }
            public float Min() { return N == 0 ? 0f : S[0]; }
            public float TopScore() { float m = 0f; for (int i = 0; i < N; i++) if (S[i] > m) m = S[i]; return m; }
            // entries at exactly score s (top-1 ties: CountAt(TopScore()))
            public int CountAt(float s) { int k = 0; for (int i = 0; i < N; i++) if (S[i] == s) k++; return k; }
            public bool HasAt(int id, float s) { for (int i = 0; i < N; i++) if (Id[i] == id && S[i] == s) return true; return false; }
            // id of the best entry (ties: the first in slot order); int.MinValue when empty
            public int TopId() { if (N == 0) return int.MinValue; int m = 0; for (int i = 1; i < N; i++) if (S[i] > S[m]) m = i; return Id[m]; }
            // top 1 equal up to ties: same top score and o's top id is one of this heap's entries at that score
            public bool SameTop(Heap o) { if (N == 0 || o.N == 0) return N == o.N; float t = TopScore(); return t == o.TopScore() && HasAt(o.TopId(), t); }
            public bool Has(int id) { for (int i = 0; i < N; i++) if (Id[i] == id) return true; return false; }
            public bool SameSet(Heap o)
            {
                if (o.N != N) return false;
                for (int i = 0; i < N; i++) if (!o.Has(Id[i])) return false;
                return true;
            }
        }

        // 1 = count rule (a), 2 = margin rule (b), 0 = no band
        public static int Fires(int cnt, int cap, float w, float drift, float margin, float s10)
        {
            if (cnt < cap) return 1;
            return w * (1f + drift + margin) >= s10 ? 2 : 0;
        }

        // relative drop worker -> live; a live fail (score 0, as C2 answers a failed Test) counts 1, a rise 0
        public static float Drift(float worker, float live)
        {
            if (!(live > 0f)) return 1f;
            if (!(worker > 0f)) return 0f;
            float d = (worker - live) / worker;
            return d > 0f ? d : 0f;
        }
        // histogram bins: 0 exact, < 1%, < 5%, < 20%, more
        public static int DriftBin(float d) { return d <= 0f ? 0 : d < 0.01f ? 1 : d < 0.05f ? 2 : d < 0.2f ? 3 : 4; }

        // docs/217 L1 late consume: the verdict at the first B14 of a B13 time slice after the resume. late = sticky give-up
        // of an earlier slice; age = frames since the resume; check = LiveCheck's reason at this slice (0 = still valid).
        // 0 = consume this slice, 1 = too old (lateTicks), 2 = the resume checks failed now, 3 = already given up.
        public const int kLateOk = 0, kLateAge = 1, kLateCheck = 2, kLateSticky = 3;
        public static int LateSlice(bool late, int frame, int resumeFrame, int lateTicks, int check)
        {
            if (late) return kLateSticky;
            int age = frame - resumeFrame;
            if (age < 0 || age > lateTicks) return kLateAge;
            return check != 0 ? kLateCheck : kLateOk;
        }

        // candidate pair indexes by score, highest first, ties by index; returns the count (insertion sort: 5-40 a search)
        public static int Order(float[] score, bool[] cand, int n, int[] order)
        {
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (!cand[i]) continue;
                int at = k++;
                while (at > 0 && score[order[at - 1]] < score[i]) { order[at] = order[at - 1]; at--; }
                order[at] = i;
            }
            return k;
        }

        public sealed class Bands
        {
            public int TopN = 10, MaxBands = 3, OrderN, Next, Fired, LastWhy;
            public float Margin, DriftMax;
            public bool Full;
            public int[] Order = new int[64];
            public void Start(int topN, int maxBands, float margin, int orderN)
            {
                TopN = topN; MaxBands = maxBands; Margin = margin; OrderN = orderN;
                Next = Math.Min(topN, orderN); Fired = 0; LastWhy = 0; DriftMax = 0f; Full = false;
            }
            public bool InBand1(int rank) { return rank < TopN; }
            public void Saw(float worker, float live) { float d = Drift(worker, live); if (d > DriftMax) DriftMax = d; }
            // the next band's pairs (eligible and unsent, from Order[Next..]) into outIdx; 0 = no band fires
            public int Step(int cnt, int cap, float s10, bool[] eligible, bool[] sent, float[] w, int[] outIdx)
            {
                if (Full) return 0;
                int j = Next;
                while (j < OrderN && (!eligible[Order[j]] || sent[Order[j]])) j++;
                if (j >= OrderN) return 0;
                int why = Fires(cnt, cap, w[Order[j]], DriftMax, Margin, s10);
                if (why == 0) return 0;
                LastWhy = why;
                bool all = Fired >= MaxBands - 1;
                int n = 0;
                for (; j < OrderN && (all || n < TopN); j++)
                {
                    int p = Order[j];
                    if (eligible[p] && !sent[p]) outIdx[n++] = p;
                }
                Next = j; Fired++; Full = all;
                return n;
            }
        }

        // replay a C1 sequence into h: only the calls with send[i] (all when send is null)
        public static void Replay(Heap h, int n, int[] id, float[] score, bool[] send)
        {
            for (int i = 0; i < n; i++) if (send == null || send[i]) h.Add(id[i], score[i]);
        }

        // the bands on a simulated queue (dry compare): live[p] is the game's own score of pair p when liveHas[p]; a pair the
        // game never scored is not offered (its object was not visited). Returns the bands fired.
        public static int RunBands(Heap h, Bands b, bool[] eligible, bool[] sent, float[] w, float[] live, bool[] liveHas, int[] buf)
        {
            while (true)
            {
                int n = b.Step(h.N, kCap, h.Min(), eligible, sent, w, buf);
                if (n == 0) break;
                for (int i = 0; i < n; i++)
                {
                    int p = buf[i];
                    sent[p] = true;
                    if (!liveHas[p]) continue;
                    h.Add(p, live[p]);
                    b.Saw(w[p], live[p]);
                }
                if (b.Full) break;
            }
            return b.Fired;
        }

        // docs/124 §17.10 crowd cost guard. A window (>= MinSearches eligible searches and >= MinFrames frames) sets the
        // sim-side cost go-live adds (cut + snapshot, the sim's GC lock waits, the sim's locked GC entries x EntryNs, our
        // consumer time incl. the band-1 C1 calls) against the scoring it saved (reference us per request x the consumed share
        // of each used search's allow-listed pairs). Cost above OffPm per mille of saved: no posting for Hold frames, so the
        // game's own B13 runs (the existing fallback); Hold doubles on each skip in a row, up to 2^kMaxShift x. The window after
        // a skip must come in at or under OnPm (hysteresis) or it skips again. No reference yet (refUsPerReq <= 0): no
        // decision. act false (switch autliveguard off): measured and reported, never skips.
        public sealed class CostGuard
        {
            public const int kMaxShift = 4;
            public int OffPm = 1000, OnPm = 800, MinSearches = 40, MinFrames = 300, Hold = 600;
            public double EntryNs = 68;          // docs/30 §15 selftest ns per locked GC entry [inferred, as mono-cost-report.ts]
            public bool Skipping, Probation;     // Probation: the first window after a skip (OnPm applies)
            // docs/222 §6.3 Own: count only measured sim cost the caller attributes per search (Add: consumer + its own cut +
            // its own GC lock waits, us) against measured vanilla scoring saved (Add: us); the native words and EntryNs are not
            // used and refUsPerReq only says whether a vanilla rate exists yet (> 0)
            public bool Own;
            public int Shift, Until, WinStart = -1, Searches, LastPm = -1, Windows, Skips;
            public double CostUs, SavedReq;      // SavedReq: requests' worth of reference scoring saved in the window
            int mCut0, mWait0, mEnt0;

            public void Reset() { Skipping = Probation = false; Shift = 0; WinStart = -1; LastPm = -1; }

            // an eligible search at frame now: false = skip it (the game's B13)
            public bool Allow(int now, bool act)
            {
                if (Skipping && act && now - Until < 0) return false;
                if (Skipping) { Skipping = false; Probation = true; WinStart = -1; }
                return true;
            }

            // per posted search: consumer us, share of its allow-listed pairs the game did not score itself (0 = dropped)
            public void Add(double consumerUs, double savedShare) { CostUs += consumerUs; SavedReq += savedShare; }

            // per allowed search, before it posts; cut/wait/entries = the native cumulative words (wrapping ints). Returns 0 =
            // window running, 1 = closed and stays on, 2 = closed and skips from now
            public int Tick(int now, int cutUs, int waitUs, int entries, double refUsPerReq, bool act)
            {
                if (WinStart < 0) { WinStart = now; mCut0 = cutUs; mWait0 = waitUs; mEnt0 = entries; Searches = 0; CostUs = SavedReq = 0; }
                Searches++;
                if (Searches < MinSearches || now - WinStart < MinFrames) return 0;
                Windows++;
                double cost = Own ? CostUs : CostUs + unchecked(cutUs - mCut0) + unchecked(waitUs - mWait0) + unchecked(entries - mEnt0) * EntryNs / 1000;
                double saved = Own ? SavedReq : refUsPerReq * SavedReq;
                int r = 1;
                if (refUsPerReq <= 0) LastPm = -1;  // no reference yet: no decision
                else
                {
                    LastPm = saved > 0 ? (int)Math.Min(cost * 1000 / saved, 1e6) : 1000000;
                    if (LastPm > (Probation ? OnPm : OffPm) && act)
                    {
                        Skipping = true; Until = now + (Hold << Shift); Skips++; r = 2;
                        if (Shift < kMaxShift) Shift++;
                    }
                    else { Probation = false; Shift = 0; }  // passed (or guard off): the next skip starts at Hold again
                }
                WinStart = now; mCut0 = cutUs; mWait0 = waitUs; mEnt0 = entries; Searches = 0; CostUs = SavedReq = 0;
                return r;
            }
        }

        // docs/222 unseen-direct go-live: the rules with no game types (selftest: node tools/selftest/autlive/run.ts)
        public static class Unseen
        {
            // the search's path at the post: direct only with the switch on, not Dry, and the actor unseen (UfWhy 0: active
            // household, selectable, seen lot, on screen, near the camera or household, held, social target seen = seen)
            public static bool AtPost(bool on, bool dry, int why) { return on && !dry && why == 0; }
            // at the resume and at each later B13 slice: unseen still = stay direct; seen = the exact path from here (snap-back)
            public static bool Stay(int why) { return why == 0; }

            // a queued direct pick at a scope check: kFlipWait (still unseen: check again later), kFlipDrop (gone, not in the
            // queue any more, or the running head: vanilla could have started it), kFlipKeep (seen, live Test passed),
            // kFlipCancel (seen, live Test failed)
            public const int kFlipWait = 0, kFlipDrop = 1, kFlipKeep = 2, kFlipCancel = 3;
            public static int Flip(bool gone, bool inQueue, bool isHead, bool seen, bool testPass)
            {
                if (gone || !inQueue || isHead) return kFlipDrop;
                if (!seen) return kFlipWait;
                return testPass ? kFlipKeep : kFlipCancel;
            }

            // D2 after the live Test of a direct pick failed `fails` times (1 = the first): kRedraw = draw again without it
            // (the next pick), kVanilla = score the remaining direct pairs live (C2) and let the game draw
            public const int kRedraw = 0, kVanilla = 1;
            public static int AfterFail(int fails, int redraws) { return fails <= redraws ? kRedraw : kVanilla; }
        }

        // docs/222 §8 pacing: a direct search skips the allow-listed ads' C1/C2, so B13's 1 ms yield timer (Autonomy.sUpdateTimer)
        // runs out later and the search ends in fewer ticks than vanilla. Each consumed object charges its skipped ads x the
        // vanilla B14 us per ad to the timer. Per slice: ChargedMs since the slice start; Forced = at the last charge the real
        // time (timer minus charges) was still within the limit and the charge took it over (a yield the charge brought forward;
        // approximate: game-scored work after the last charge is not seen).
        public sealed class Pace
        {
            public double ChargedMs;
            public bool Forced;
            public void Slice() { ChargedMs = 0; Forced = false; }
            // timerMs = the timer before this charge; returns the ms to add (0 = no rate yet or nothing skipped)
            public double Charge(double usPerAd, int ads, double timerMs, double limitMs)
            {
                if (usPerAd <= 0 || ads <= 0) return 0;
                double add = usPerAd * ads / 1000.0;
                Forced = timerMs - ChargedMs <= limitMs && timerMs + add > limitMs;
                ChargedMs += add;
                return add;
            }
        }

        // vanilla B14 cost per meta ad (us), from the game's own B14 calls (sampled pass-through and objects a go-live search
        // hands to the game); the per-path cost guard's saved side (docs/222 §6.3). Rate 0 until MinPairs ads were timed.
        public sealed class B14Rate
        {
            public long Us, Pairs;
            public int MinPairs = 200;
            public void Reset() { Us = Pairs = 0; }
            public void Add(long us, int pairs) { if (pairs > 0 && us >= 0) { Us += us; Pairs += pairs; } }
            public double PerPair { get { return Pairs >= MinPairs ? Us / (double)Pairs : 0; } }
        }
    }
}
