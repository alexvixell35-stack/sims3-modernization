// Usage: node tools/selftest/autlive/run.ts  (compiles this with mods/ts3fixes/src/AutLiveCore.cs)
// Offline self-check of the docs/124 §16-§17 go-live consume logic: the BestScores queue model, the worker order, the band
// rule, and the exactness claim of §16.1: with live scores equal to the worker's (or all scaled by one factor), the queue
// that consume builds (band 1 at B14, bands after B13) equals the queue the game builds from every passing pair. Exit 1 on
// failure.
using System;
using Ts3Fixes;

static class AutLiveTest
{
    static int sFail;
    static void Check(string what, bool ok) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        sFail += TunFpTest.Run();
        // queue: scores > 0 only; full = replace the smallest only when strictly greater
        AutLiveCore.Heap h = new AutLiveCore.Heap(3);
        h.Add(1, 5f); h.Add(2, 3f); h.Add(3, 0f); h.Add(4, -1f); h.Add(5, 4f);
        Check("heap fills with positives", h.N == 3 && h.Has(1) && h.Has(2) && h.Has(5) && !h.Has(3));
        h.Add(6, 2f);
        Check("smaller than the minimum: ignored", !h.Has(6));
        h.Add(7, 4f);
        Check("greater than the minimum: replaces it", h.Has(7) && !h.Has(2) && h.Min() == 4f);
        h.Add(8, 4f);
        Check("equal to the minimum: ignored (strict)", !h.Has(8));
        Check("top", h.TopId() == 1);
        // heap order and ties as the game's PriorityQueue
        h = new AutLiveCore.Heap(3);
        h.Add(1, 2f); h.Add(2, 2f);
        Check("sift up: an equal child stays below", h.Id[0] == 1 && h.Id[1] == 2);
        h.Add(3, 1f);
        Check("sift up: strictly smaller rises to the root", h.Id[0] == 3 && h.Id[2] == 1);
        h = new AutLiveCore.Heap(3);
        h.Add(1, 1f); h.Add(2, 2f); h.Add(3, 2f);
        h.Add(4, 3f);  // root replaced; children tie at 2: the right one (3) moves up
        Check("sift down: equal children take the right", h.Id[0] == 3 && h.Id[1] == 2 && h.Id[2] == 4);
        h.Add(5, 4f);  // min tie 2/2: the root (3) goes, not the first 2 in insertion order
        Check("min tie: the root minimum is evicted", h.Has(2) && !h.Has(3) && h.Has(4) && h.Has(5));
        h = new AutLiveCore.Heap(3);
        h.Add(1, 1f); h.Add(2, 3f); h.Add(3, 3f);
        h.Add(4, 3f);  // root 1 replaced by 3: children equal to it, no swap
        Check("sift down: no swap with an equal child", h.Id[0] == 4);
        Check("top ties", h.TopScore() == 3f && h.CountAt(3f) == 3 && h.TopId() == 4);
        AutLiveCore.Heap h2 = new AutLiveCore.Heap(3);
        h2.Add(2, 3f); h2.Add(3, 3f); h2.Add(4, 3f);
        Check("same top up to ties", h2.SameTop(h) && h.SameTop(h2) && h2.TopId() != h.TopId());
        h2 = new AutLiveCore.Heap(3); h2.Add(9, 3f);
        Check("top id must be among the other's top entries", !h2.SameTop(h) && !h.SameTop(h2));
        AutLiveCore.Heap h3 = new AutLiveCore.Heap(3);
        for (int i = 0; i < h.N; i++) h3.Add(h.Id[i], h.S[i]);
        Check("copy in slot order keeps the slot order", h3.Id[0] == h.Id[0] && h3.Id[1] == h.Id[1] && h3.Id[2] == h.Id[2]);
        // rule
        Check("count rule", AutLiveCore.Fires(9, 10, 1f, 0f, 0f, 100f) == 1);
        Check("tie fires", AutLiveCore.Fires(10, 10, 2f, 0f, 0f, 2f) == 2);
        Check("below: no band", AutLiveCore.Fires(10, 10, 1.9f, 0f, 0f, 2f) == 0);
        Check("drift widens", AutLiveCore.Fires(10, 10, 1.9f, 0.1f, 0f, 2f) == 2);
        Check("margin widens", AutLiveCore.Fires(10, 10, 1.9f, 0f, 0.06f, 2f) == 2);
        Check("drift fail = 1", AutLiveCore.Drift(5f, 0f) == 1f && AutLiveCore.Drift(5f, 6f) == 0f && Math.Abs(AutLiveCore.Drift(4f, 3f) - 0.25f) < 1e-6f);
        Check("drift bins", AutLiveCore.DriftBin(0f) == 0 && AutLiveCore.DriftBin(0.005f) == 1 && AutLiveCore.DriftBin(0.03f) == 2 && AutLiveCore.DriftBin(0.1f) == 3 && AutLiveCore.DriftBin(0.5f) == 4);
        // order: score desc, ties by index
        int[] ord = new int[8];
        int on = AutLiveCore.Order(new float[] { 1f, 3f, 3f, 2f, 9f }, new bool[] { true, true, true, true, false }, 5, ord);
        Check("order", on == 4 && ord[0] == 1 && ord[1] == 2 && ord[2] == 3 && ord[3] == 0);
        // bands: max 2 = band 1 + one more, then full
        {
            AutLiveCore.Bands b = new AutLiveCore.Bands();
            float[] w = new float[30];
            bool[] el = new bool[30], sent = new bool[30];
            for (int i = 0; i < 30; i++) { w[i] = 30 - i; el[i] = true; }
            for (int i = 0; i < 30; i++) b.Order[i] = i;
            b.Start(4, 2, 0f, 30);
            for (int i = 0; i < 4; i++) sent[i] = true;
            int[] buf = new int[30];
            int n = b.Step(10, 10, 100f, el, sent, w, buf);
            Check("no fire above s10", n == 0);
            n = b.Step(3, 10, 100f, el, sent, w, buf);
            Check("count rule sends the next 4", n == 4 && buf[0] == 4 && buf[3] == 7 && b.LastWhy == 1 && !b.Full);
            for (int i = 0; i < n; i++) sent[buf[i]] = true;
            el[9] = false;
            n = b.Step(10, 10, 22f, el, sent, w, buf);  // w = 22 (pair 8) >= 22: tie fires, the last band = all remaining eligible
            Check("full band: every remaining eligible pair", n == 21 && buf[0] == 8 && buf[1] == 10 && b.Full && b.LastWhy == 2);
            Check("after full: nothing", b.Step(0, 10, 0f, el, new bool[30], w, buf) == 0);
        }

        // exactness (§16.1): random searches; vanilla = every pair through C1 in request order plus local and off-list entries;
        // consume = the same calls minus the allow-listed passing pairs outside band 1, then the bands after B13
        Random rnd = new Random(12345);
        int exactRuns = 0, exactMiss = 0, scaledMiss = 0, noisyRuns = 0, noisyMiss = 0, fired = 0;
        for (int run = 0; run < 3000; run++)
        {
            int pairs = rnd.Next(0, 60), local = rnd.Next(0, 13), off = rnd.Next(0, 20);
            int topN = 1 + rnd.Next(12), bands = 1 + rnd.Next(4);
            float[] w = new float[pairs], live = new float[pairs];
            bool[] pass = new bool[pairs], liveHas = new bool[pairs];
            for (int i = 0; i < pairs; i++)
            {
                pass[i] = rnd.Next(3) > 0;
                w[i] = pass[i] ? (float)(1 + rnd.NextDouble() * 99) + i * 1e-3f : 0f;
                liveHas[i] = true;
            }
            int mode = run % 3;  // 0 exact, 1 one scale factor, 2 noisy per pair (measured, not asserted)
            float f = (float)(0.5 + rnd.NextDouble() * 0.5);
            for (int i = 0; i < pairs; i++)
                live[i] = !pass[i] ? 0f : mode == 0 ? w[i] : mode == 1 ? w[i] * f : w[i] * (float)(0.8 + rnd.NextDouble() * 0.4);
            // calls in the game's order: local entries first (before B13), then a shuffle of meta pairs and off-list ads
            int calls = local + pairs + off;
            int[] id = new int[calls];
            float[] sc = new float[calls];
            bool[] send = new bool[calls];
            int k = 0;
            for (int i = 0; i < local; i++) { id[k] = -1 - i; sc[k] = (float)(rnd.NextDouble() * 120); send[k] = true; k++; }
            int[] mix = new int[pairs + off];
            for (int i = 0; i < mix.Length; i++) mix[i] = i;
            for (int i = mix.Length - 1; i > 0; i--) { int j = rnd.Next(i + 1); int t = mix[i]; mix[i] = mix[j]; mix[j] = t; }
            bool[] cand = new bool[pairs];
            for (int i = 0; i < pairs; i++) cand[i] = pass[i];
            AutLiveCore.Bands b = new AutLiveCore.Bands();
            if (b.Order.Length < pairs) b.Order = new int[pairs];
            int n = AutLiveCore.Order(w, cand, pairs, b.Order);
            b.Start(topN, bands, 0f, n);
            bool[] sel = new bool[pairs], sent = new bool[pairs];
            for (int r = 0; r < n && r < topN; r++) sel[b.Order[r]] = true;
            foreach (int m in mix)
            {
                if (m < pairs) { id[k] = m; sc[k] = live[m]; send[k] = sel[m]; if (sel[m]) { sent[m] = true; b.Saw(w[m], live[m]); } }
                else { id[k] = -100 - m; sc[k] = (float)(rnd.NextDouble() * 120); send[k] = true; }
                k++;
            }
            AutLiveCore.Heap van = new AutLiveCore.Heap(10), con = new AutLiveCore.Heap(10);
            AutLiveCore.Replay(van, calls, id, sc, null);
            AutLiveCore.Replay(con, calls, id, sc, send);
            fired += AutLiveCore.RunBands(con, b, cand, sent, w, live, liveHas, new int[pairs]);
            bool same = van.SameSet(con) && van.TopId() == con.TopId();
            if (mode == 0) { exactRuns++; if (!same) exactMiss++; }
            else if (mode == 1) { if (!same) scaledMiss++; }
            else { noisyRuns++; if (!same) noisyMiss++; }
        }
        Check("exact live scores: consume queue = game queue in every run", exactMiss == 0);
        Check("one scale factor: consume queue = game queue in every run", scaledMiss == 0);
        Console.WriteLine("autlive: " + exactRuns + " exact runs (misses " + exactMiss + "), scaled misses " + scaledMiss + ", noisy +-20%: "
            + noisyMiss + " of " + noisyRuns + " differ (not asserted), bands fired " + fired);
        GuardTests();
        LateTests();
        UnseenTests();
        // per-search feature mask: native bits mapped, managed bits, names, JSON, per-bit counts
        {
            int m = AutLiveCore.Feat.Build(1 | 4 | 8, 2, true, false, true);
            Check("feat build", m == ((1 << AutLiveCore.Feat.PreLearn) | (1 << AutLiveCore.Feat.Satb) | (1 << AutLiveCore.Feat.Cold)
                | (1 << AutLiveCore.Feat.Rain) | (1 << AutLiveCore.Feat.DesireSkip) | (1 << AutLiveCore.Feat.Partial)));
            Check("feat build empty", AutLiveCore.Feat.Build(0, 0, false, false, false) == 0);
            Check("feat tierreqlots + guard", AutLiveCore.Feat.Build(2, 0, false, true, false) == ((1 << AutLiveCore.Feat.TierReqLots) | (1 << AutLiveCore.Feat.Guard)));
            Check("feat text", AutLiveCore.Feat.Text(0x109) == "late|rain|partial" && AutLiveCore.Feat.Text(0) == "");
            Check("feat json", AutLiveCore.Feat.Json(0x109) == ",\"feat\":\"0x109\",\"feats\":\"late|rain|partial\"");
            Check("feat names = N", AutLiveCore.Feat.Names.Length == AutLiveCore.Feat.N);
            int[] c = new int[20];
            AutLiveCore.Feat.Count(c, 5, 0x109);
            AutLiveCore.Feat.Count(c, 15, 0x1FF);  // past the end: no throw, cut at the array
            Check("feat count", c[5] == 1 && c[8] == 1 && c[13] == 1 && c[6] == 0 && c[15] == 1 && c[19] == 1);
        }
        Console.WriteLine(sFail == 0 ? "autlive selftest: PASS" : "autlive selftest: " + sFail + " FAIL");
        return sFail == 0 ? 0 : 1;
    }

    // docs/124 §17.10 cost guard. One eligible search every 10 frames; the native cumulative words grow by the search's cut us
    // (wait and entries fixed per search). Returns searches skipped; sets sFrame.
    static int sFrame, sCut, sSkips2;
    static int Run(AutLiveCore.CostGuard g, int searches, int cutUs, double consumerUs, double share, double refUs, bool act)
    {
        int skipped = 0;
        for (int i = 0; i < searches; i++, sFrame += 10)
        {
            if (!g.Allow(sFrame, act)) { skipped++; continue; }
            int r = g.Tick(sFrame, sCut, 0, 0, refUs, act);
            if (r == 2) { sSkips2++; skipped++; continue; }
            sCut = unchecked(sCut + cutUs);
            g.Add(consumerUs, share);
        }
        return skipped;
    }

    // docs/217 L1: the late-slice rule
    static void LateTests()
    {
        Check("late: next slice within LateTicks consumes", AutLiveCore.LateSlice(false, 101, 100, 3, 0) == AutLiveCore.kLateOk);
        Check("late: slice at exactly LateTicks consumes", AutLiveCore.LateSlice(false, 103, 100, 3, 0) == AutLiveCore.kLateOk);
        Check("late: slice past LateTicks falls back (age)", AutLiveCore.LateSlice(false, 104, 100, 3, 0) == AutLiveCore.kLateAge);
        Check("late: LateTicks 0 never consumes a later slice", AutLiveCore.LateSlice(false, 101, 100, 0, 0) == AutLiveCore.kLateAge);
        Check("late: frame before the resume (wrap or reuse) falls back", AutLiveCore.LateSlice(false, 99, 100, 3, 0) == AutLiveCore.kLateAge);
        Check("late: failed resume check falls back (check)", AutLiveCore.LateSlice(false, 101, 100, 3, 10) == AutLiveCore.kLateCheck);
        Check("late: a given-up search stays given up", AutLiveCore.LateSlice(true, 101, 100, 3, 0) == AutLiveCore.kLateSticky);
    }

    static void GuardTests()
    {
        // window: 40 searches and 300 frames (one search every 10 frames: 40 searches = 400 frames)
        AutLiveCore.CostGuard g = new AutLiveCore.CostGuard();
        sFrame = 1000; sCut = 0;
        Run(g, 39, 2000, 0, 1, 1000, true);
        Check("guard: no decision before MinSearches", g.Windows == 0);
        g = new AutLiveCore.CostGuard(); sFrame = 1000;
        Run(g, 400, 2000, 0, 0, 0, true);
        Check("guard: no reference = no decision, ratio -1", g.Windows > 0 && g.Skips == 0 && g.LastPm == -1);
        // cheap: 500 us cost per search vs 1000 us saved -> 500 pm, stays on
        g = new AutLiveCore.CostGuard(); sFrame = 1000;
        Check("guard: cheap stays on", Run(g, 400, 400, 100, 1, 1000, true) == 0 && g.Skips == 0 && g.LastPm == 500);
        // the BP crowd G7 numbers per search [docs/124 §17.10]: cost ~2.66 ms (36.5 ms/s at 13.7/s) vs saved ~2.98 ms -> ~894 pm, on
        g = new AutLiveCore.CostGuard(); sFrame = 1000;
        Check("guard: crowd G7 (~894 pm) stays on at OffPm 1000", Run(g, 400, 2660, 0, 1, 2980, true) == 0 && g.LastPm >= 890 && g.LastPm <= 895);
        // expensive: 1500 pm -> skip for Hold frames (600 = 60 searches), then probation
        g = new AutLiveCore.CostGuard(); sFrame = 1000; sSkips2 = 0;
        int sk = Run(g, 40, 1500, 0, 1, 1000, true);
        Check("guard: expensive skips at the window end", g.Skips == 1 && g.Skipping && g.LastPm == 1500 && sk == 1 && g.Until == sFrame - 10 + 600);
        sk = Run(g, 59, 1500, 0, 1, 1000, true);
        Check("guard: skips through the hold", sk == 59 && g.Skipping);
        // probation at 900 pm (between OnPm 800 and OffPm 1000): skips again with the hold doubled

        Run(g, 1, 900, 0, 1, 1000, true);
        Check("guard: probation after the hold", !g.Skipping && g.Probation);
        Run(g, 39, 900, 0, 1, 1000, true);
        Check("guard: hysteresis: 900 pm after a skip skips again, hold doubled", g.Skips == 2 && g.Skipping && g.Until - (sFrame - 10) == 1200);
        Run(g, 120, 700, 0, 1, 1000, true);
        Run(g, 41, 700, 0, 1, 1000, true);
        Check("guard: 700 pm in probation: back on, hold reset", !g.Skipping && !g.Probation && g.Shift == 0 && g.LastPm == 700);
        // a skip window in the on state needs > OffPm: 900 pm stays on
        Run(g, 41, 900, 0, 1, 1000, true);
        Check("guard: 900 pm in the on state stays on", !g.Skipping && g.Skips == 2);
        // hold doubles up to 16 x
        g = new AutLiveCore.CostGuard(); sFrame = 1000;
        for (int i = 0; i < 7; i++) { Run(g, 41, 3000, 0, 1, 1000, true); while (g.Skipping) Run(g, 1, 3000, 0, 1, 1000, true); }
        Check("guard: hold capped at 16 x", g.Shift == AutLiveCore.CostGuard.kMaxShift && g.Skips >= 5);
        // nothing consumed (every search dropped) with a reference: cost with no saving -> skip
        g = new AutLiveCore.CostGuard(); sFrame = 1000;
        Run(g, 41, 500, 0, 0, 1000, true);
        Check("guard: nothing saved = skip", g.Skipping && g.LastPm == 1000000);
        // switch off (A/B arm): measured, never skips; turning it off mid-skip ends the skip
        g = new AutLiveCore.CostGuard(); sFrame = 1000;
        Check("guard: act off never skips", Run(g, 400, 3000, 0, 1, 1000, false) == 0 && g.Skips == 0 && g.LastPm == 3000);
        g = new AutLiveCore.CostGuard(); sFrame = 1000;
        Run(g, 41, 3000, 0, 1, 1000, true);
        Check("guard: off mid-skip posts again", g.Skipping && g.Allow(sFrame, false) && !g.Skipping);
        // consumer time and wrapping native words count; MinFrames holds a window open
        g = new AutLiveCore.CostGuard(); sFrame = 1000; sCut = int.MaxValue - 10000;
        Run(g, 41, 400, 600, 1, 1000, true);
        Check("guard: wrapped cut counter + consumer time = 1000 pm", g.LastPm == 1000 && !g.Skipping);
        g = new AutLiveCore.CostGuard(); g.MinFrames = 100000; sFrame = 1000;
        Run(g, 400, 3000, 0, 1, 1000, true);
        Check("guard: MinFrames keeps the window open", g.Windows == 0);
    }

    // docs/222 unseen-direct: scope at the post and the resume, snap-back of queued picks, D2 redraw rule, per-path own guard
    static void UnseenTests()
    {
        Check("unseen: switch on, acting, unseen = direct", AutLiveCore.Unseen.AtPost(true, false, 0));
        Check("unseen: switch off = exact", !AutLiveCore.Unseen.AtPost(false, false, 0));
        Check("unseen: Dry never direct", !AutLiveCore.Unseen.AtPost(true, true, 0));
        for (int why = 1; why <= 9; why++)  // every seen reason of UfWhy (household, active lot, on screen, near, held, social, ...)
            Check("unseen: seen reason " + why + " = exact", !AutLiveCore.Unseen.AtPost(true, false, why));
        Check("unseen: classifier error (-1) = exact", !AutLiveCore.Unseen.AtPost(true, false, -1));
        Check("unseen: stays direct while unseen", AutLiveCore.Unseen.Stay(0));
        Check("unseen: first seen answer leaves direct", !AutLiveCore.Unseen.Stay(3) && !AutLiveCore.Unseen.Stay(-1));
        // docs/124 §17.12 autliveseen: only a proven seen NPC leaves the game's search while autseengame is on
        Check("seen: autseengame off = exact (old path)", AutLiveCore.Unseen.SeenPath(false, false, false, false) == AutLiveCore.Unseen.kSeenExact);
        Check("seen: autseengame on, autliveseen off = game", AutLiveCore.Unseen.SeenPath(true, false, false, true) == AutLiveCore.Unseen.kSeenGame
            && AutLiveCore.Unseen.SeenPath(true, false, true, true) == AutLiveCore.Unseen.kSeenGame);
        Check("seen: household / unknown scope (seenNpc false) = game", AutLiveCore.Unseen.SeenPath(true, true, false, false) == AutLiveCore.Unseen.kSeenGame
            && AutLiveCore.Unseen.SeenPath(true, true, true, false) == AutLiveCore.Unseen.kSeenGame);
        Check("seen: seen NPC = exact", AutLiveCore.Unseen.SeenPath(true, true, false, true) == AutLiveCore.Unseen.kSeenExact);
        Check("seen: seen NPC, dry = exact dry", AutLiveCore.Unseen.SeenPath(true, true, true, true) == AutLiveCore.Unseen.kSeenDry);
        Check("seen: household at the resume drops", !AutLiveCore.Unseen.SeenKeep(true) && AutLiveCore.Unseen.SeenKeep(false));
        Check("flip: still unseen waits", AutLiveCore.Unseen.Flip(false, true, false, false, false) == AutLiveCore.Unseen.kFlipWait);
        Check("flip: seen + live Test pass keeps", AutLiveCore.Unseen.Flip(false, true, false, true, true) == AutLiveCore.Unseen.kFlipKeep);
        Check("flip: seen + live Test fail cancels", AutLiveCore.Unseen.Flip(false, true, false, true, false) == AutLiveCore.Unseen.kFlipCancel);
        Check("flip: running head is never cancelled", AutLiveCore.Unseen.Flip(false, true, true, true, false) == AutLiveCore.Unseen.kFlipDrop);
        Check("flip: gone or out of the queue drops the tag", AutLiveCore.Unseen.Flip(true, true, false, true, false) == AutLiveCore.Unseen.kFlipDrop
            && AutLiveCore.Unseen.Flip(false, false, false, true, false) == AutLiveCore.Unseen.kFlipDrop);
        Check("redraw: first fail draws the next pick", AutLiveCore.Unseen.AfterFail(1, 1) == AutLiveCore.Unseen.kRedraw);
        Check("redraw: second fail goes vanilla", AutLiveCore.Unseen.AfterFail(2, 1) == AutLiveCore.Unseen.kVanilla);
        Check("redraw: 0 redraws = vanilla at once", AutLiveCore.Unseen.AfterFail(1, 0) == AutLiveCore.Unseen.kVanilla);
        AutLiveCore.B14Rate r = new AutLiveCore.B14Rate();
        r.Add(1000, 100);
        Check("b14 rate: no rate under MinPairs", r.PerPair == 0);
        r.Add(1000, 100); r.Add(-5, 10); r.Add(50, 0);
        Check("b14 rate: us per ad, bad samples ignored", Math.Abs(r.PerPair - 10.0) < 1e-9);
        // own guard: native words ignored, cost/saved are the caller's us; no rate yet = no decision
        AutLiveCore.CostGuard g = new AutLiveCore.CostGuard(); g.Own = true; sFrame = 1000; sCut = 0;
        Run(g, 400, 999999, 900, 1000, 1, true);
        Check("own guard: cost 900 us vs saved 1000 us = 900 pm, no skip (cut words ignored)", g.LastPm == 900 && g.Skips == 0);
        g = new AutLiveCore.CostGuard(); g.Own = true; sFrame = 1000;
        Run(g, 41, 0, 1500, 1000, 1, true);
        Check("own guard: cost above saved skips", g.Skipping && g.LastPm == 1500);
        g = new AutLiveCore.CostGuard(); g.Own = true; sFrame = 1000;
        Run(g, 400, 0, 1500, 1000, 0, true);
        Check("own guard: no vanilla rate = no decision", g.LastPm == -1 && g.Skips == 0);
        // docs/222 §8 pacing: skipped ads x vanilla us per ad charged to B13's 1 ms timer; forced = the charge took it over
        AutLiveCore.Pace pc = new AutLiveCore.Pace();
        Check("pace: no rate = no charge", pc.Charge(0, 10, 0.2, 1) == 0 && pc.ChargedMs == 0 && !pc.Forced);
        Check("pace: no skipped ads = no charge", pc.Charge(30, 0, 0.2, 1) == 0);
        Check("pace: 10 ads x 30 us = 0.3 ms", Math.Abs(pc.Charge(30, 10, 0.2, 1) - 0.3) < 1e-9 && !pc.Forced);
        // timer now 0.2 + 0.3 = 0.5 (+ real 0.1 = 0.6): 20 ads x 30 us = 0.6 ms takes it to 1.2, real 0.3 = forced
        Check("pace: charge over the limit with real time under it = forced", Math.Abs(pc.Charge(30, 20, 0.6, 1) - 0.6) < 1e-9 && pc.Forced
            && Math.Abs(pc.ChargedMs - 0.9) < 1e-9);
        pc.Slice();
        Check("pace: a new slice clears", pc.ChargedMs == 0 && !pc.Forced);
        pc.Charge(30, 5, 1.5, 1);
        Check("pace: real time already over the limit = not forced", !pc.Forced);
        // docs/222 §11.8 credit: ticks waited for the worker paid first, across slices
        AutLiveCore.Pace pw = new AutLiveCore.Pace();
        pw.CreditMs = 2;
        Check("pace credit: charge inside the credit = nothing added", pw.Charge(30, 50, 0.2, 1) == 0 && Math.Abs(pw.CreditMs - 0.5) < 1e-9 && !pw.Forced);
        pw.Slice();
        Check("pace credit: a new slice keeps it", Math.Abs(pw.CreditMs - 0.5) < 1e-9);
        Check("pace credit: the rest of the charge is added", Math.Abs(pw.Charge(30, 50, 0.2, 1) - 1.0) < 1e-9 && pw.CreditMs == 0 && pw.Forced);
        Check("queue bins", AutLiveCore.QueueBin(0) == 0 && AutLiveCore.QueueBin(1) == 0 && AutLiveCore.QueueBin(2) == 1 && AutLiveCore.QueueBin(9) == 1
            && AutLiveCore.QueueBin(10) == 2 && AutLiveCore.QueueBin(49) == 2 && AutLiveCore.QueueBin(50) == 3 && AutLiveCore.QueueBin(199) == 3 && AutLiveCore.QueueBin(200) == 4);
        // docs/124 §7.2 G8 pick groups: household scope wins over unseen; every index distinct and named
        Check("pick groups", AutLiveCore.PickGroup.Name(AutLiveCore.PickGroup.Index(AutLiveCore.PickGroup.Class(true, false), AutLiveCore.PickGroup.kDirect)) == "u.d"
            && AutLiveCore.PickGroup.Name(AutLiveCore.PickGroup.Index(AutLiveCore.PickGroup.Class(false, false), AutLiveCore.PickGroup.kExact)) == "s.x"
            && AutLiveCore.PickGroup.Name(AutLiveCore.PickGroup.Index(AutLiveCore.PickGroup.Class(true, true), AutLiveCore.PickGroup.kGame)) == "h.g"
            && AutLiveCore.PickGroup.NewTables().Length == AutLiveCore.PickGroup.kN
            && AutLiveCore.PickGroup.Index(2, AutLiveCore.PickGroup.kGame) == AutLiveCore.PickGroup.kN - 1);
        pc.Slice(); pc.Charge(30, 10, 0.3, 1); pc.Charge(30, 1, 0.9, 1);
        Check("pace: forced only when this charge crosses (0.9 + 0.03 stays under)", !pc.Forced);
        // docs/222 §9 per-definition compare: mismatch directions, both-pass ratios, age bins
        AutLiveCore.DefCmp dc = new AutLiveCore.DefCmp();
        Check("defcmp: no ratios = NaN", float.IsNaN(dc.Quantile(0.5)));
        dc.Note(false, true, 0, 2, 1); dc.Note(true, false, 3, 0, 5); dc.Note(false, false, 0, 0, 2); dc.Note(false, true, 0, 1, 9);
        for (int i = 1; i <= 10; i++) dc.Note(true, true, i, 2, 3);  // ratios 0.5 .. 5.0
        Check("defcmp: counts", dc.N == 14 && dc.WfGp == 2 && dc.WpGf == 1 && dc.BothFail == 1 && dc.BothPass == 10 && dc.Mis == 3);
        Check("defcmp: age bins all", dc.Age[0] == 1 && dc.Age[1] == 1 && dc.Age[2] == 10 && dc.Age[3] == 1 && dc.Age[4] == 1);
        Check("defcmp: age bins mismatches", dc.AgeMis[0] == 1 && dc.AgeMis[3] == 1 && dc.AgeMis[4] == 1 && dc.AgeMis[1] + dc.AgeMis[2] == 0);
        Check("defcmp: median, p90 nearest rank", dc.Quantile(0.5) == 2.5f && dc.Quantile(0.9) == 4.5f && dc.Quantile(1) == 5f);
        Check("defcmp: age bin edges", AutLiveCore.DefCmp.AgeBin(0) == 0 && AutLiveCore.DefCmp.AgeBin(4) == 3 && AutLiveCore.DefCmp.AgeBin(7) == 3 && AutLiveCore.DefCmp.AgeBin(8) == 4);
    }
}

// docs/124 §3.1 tuning fingerprint (AutLiveCore.TunFp): a model of AutLive.cs TuneFp over mock types with the game's field
// shapes (private enum flags read by FieldInfo + Convert.ToInt32, ulong enums mixed as two words, floats as bits). Proves:
// same tuning = same hash; an in-place change of any covered field = a different hash (restored = the old hash again);
// reports ns per fingerprint on this host (.NET Framework; the game's Mono 2.x is slower, inferred 2-5x).
static class TunFpTest
{
    enum TFlags : byte { None = 0, DisallowAutonomous = 1, DisallowUserDirected = 2 }
    enum AFlags : ushort { None = 0, A = 1, B = 0x100 }
    enum Big : ulong { None = 0, Hi = 1UL << 40 }
    class Change { public Big Commodity; public float ConstantChange, mMultiplier; }
    class Avail
    {
        AFlags mFlags; public void Set(AFlags f) { mFlags = f; } public AFlags Get() { return mFlags; }
        public uint AgeSpecies; public Big MotiveType, MoodType, SkillType, CareerType; public float MotiveValue, MoodValue;
        public int SkillValue, CareerValue; public int WorldType; public uint Occult; public int OccultType; public short Snow;
        public System.Collections.Generic.List<int> ExTraits = new System.Collections.Generic.List<int>(), ReqTraits, ExBuffs, ReqBuffs;
    }
    class Trade { public float TimeEstimate = 1; public System.Collections.Generic.List<Change> mOutputs = new System.Collections.Generic.List<Change>(); }
    class Tun { TFlags mFlags; public void Set(TFlags f) { mFlags = f; } public bool AlwaysChooseBest; public Avail Availability = new Avail(); public Trade mTradeoff = new Trade(); }
    static readonly System.Reflection.FieldInfo fT = typeof(Tun).GetField("mFlags", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
        fA = typeof(Avail).GetField("mFlags", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static int sFields;
    static uint Fp(Tun t)
    {
        int n = 0;
        uint h = AutLiveCore.TunFp.kSeed;
        h = AutLiveCore.TunFp.Mix(h, Convert.ToInt32(fT.GetValue(t))); h = AutLiveCore.TunFp.Mix(h, t.AlwaysChooseBest ? 1 : 0); n += 2;
        Avail a = t.Availability;
        h = AutLiveCore.TunFp.Mix(h, Convert.ToInt32(fA.GetValue(a))); h = AutLiveCore.TunFp.Mix64(h, (long)a.AgeSpecies);
        h = AutLiveCore.TunFp.Mix64(h, (long)a.MotiveType); h = AutLiveCore.TunFp.MixF(h, a.MotiveValue);
        h = AutLiveCore.TunFp.Mix64(h, (long)a.MoodType); h = AutLiveCore.TunFp.MixF(h, a.MoodValue);
        h = AutLiveCore.TunFp.Mix64(h, (long)a.SkillType); h = AutLiveCore.TunFp.Mix(h, a.SkillValue);
        h = AutLiveCore.TunFp.Mix64(h, (long)a.CareerType); h = AutLiveCore.TunFp.Mix(h, a.CareerValue);
        h = AutLiveCore.TunFp.Mix64(h, a.WorldType); h = AutLiveCore.TunFp.Mix64(h, (long)a.Occult); h = AutLiveCore.TunFp.Mix64(h, a.OccultType);
        h = AutLiveCore.TunFp.Mix(h, a.Snow);
        h = AutLiveCore.TunFp.Mix(h, a.ExTraits != null ? a.ExTraits.Count : -1); h = AutLiveCore.TunFp.Mix(h, a.ReqTraits != null ? a.ReqTraits.Count : -1);
        h = AutLiveCore.TunFp.Mix(h, a.ExBuffs != null ? a.ExBuffs.Count : -1); h = AutLiveCore.TunFp.Mix(h, a.ReqBuffs != null ? a.ReqBuffs.Count : -1);
        n += 18;
        Trade tr = t.mTradeoff;
        h = AutLiveCore.TunFp.MixF(h, tr.TimeEstimate); h = AutLiveCore.TunFp.Mix(h, tr.mOutputs.Count); n += 2;
        for (int i = 0; i < tr.mOutputs.Count; i++)
        {
            Change c = tr.mOutputs[i];
            h = AutLiveCore.TunFp.Mix64(h, (long)c.Commodity); h = AutLiveCore.TunFp.MixF(h, c.ConstantChange); h = AutLiveCore.TunFp.MixF(h, c.mMultiplier); n += 3;
        }
        sFields = n;
        return AutLiveCore.TunFp.End(h);
    }
    static Tun Make()
    {
        Tun t = new Tun();
        t.Availability.AgeSpecies = 0x7F; t.Availability.MotiveValue = -50; t.Availability.SkillValue = 3;
        for (int i = 0; i < 3; i++) { Change c = new Change(); c.Commodity = (Big)(i + 1); c.ConstantChange = 10 * (i + 1); c.mMultiplier = 1; t.mTradeoff.mOutputs.Add(c); }
        return t;
    }
    public static int Run()
    {
        int fail = 0;
        Tun t = Make();
        uint h0 = Fp(t);
        if (Fp(Make()) != h0 || Fp(t) != h0) { fail++; Console.WriteLine("FAIL tunfp: same tuning, same hash"); }
        if (sFields != AutLiveCore.TunFp.kScalars + 3 * AutLiveCore.TunFp.kPerOutput) { fail++; Console.WriteLine("FAIL tunfp: field count " + sFields); }
        // every covered field, Retuner-style in place: changed = new hash, restored = old hash
        Action<Tun>[] edit = {
            x => x.Set(TFlags.DisallowAutonomous), x => x.AlwaysChooseBest = true, x => x.Availability.Set(AFlags.B),
            x => x.Availability.AgeSpecies = 0x3F, x => x.Availability.MotiveType = Big.Hi, x => x.Availability.MotiveValue = -40,
            x => x.Availability.MoodType = (Big)2, x => x.Availability.MoodValue = 1, x => x.Availability.SkillType = Big.Hi, x => x.Availability.SkillValue = 4,
            x => x.Availability.CareerType = (Big)5, x => x.Availability.CareerValue = 1, x => x.Availability.WorldType = 1, x => x.Availability.Occult = 2,
            x => x.Availability.OccultType = 1, x => x.Availability.Snow = 1, x => x.Availability.ExTraits.Add(1), x => x.Availability.ReqTraits = new System.Collections.Generic.List<int>(),
            x => x.Availability.ExBuffs = new System.Collections.Generic.List<int>(), x => x.Availability.ReqBuffs = new System.Collections.Generic.List<int>(),
            x => x.mTradeoff.TimeEstimate = 2, x => x.mTradeoff.mOutputs.RemoveAt(2),
            x => x.mTradeoff.mOutputs[1].Commodity = Big.Hi, x => x.mTradeoff.mOutputs[1].ConstantChange = 25, x => x.mTradeoff.mOutputs[2].mMultiplier = 1.5f };
        for (int i = 0; i < edit.Length; i++)
        {
            Tun x = Make();
            edit[i](x);
            if (Fp(x) == h0) { fail++; Console.WriteLine("FAIL tunfp: edit " + i + " kept the hash"); }
        }
        t.mTradeoff.mOutputs[1].ConstantChange = 25;
        uint h1 = Fp(t);
        t.mTradeoff.mOutputs[1].ConstantChange = 20;
        if (h1 == h0 || Fp(t) != h0) { fail++; Console.WriteLine("FAIL tunfp: in-place edit and restore"); }
        if (edit.Length != AutLiveCore.TunFp.kScalars + AutLiveCore.TunFp.kPerOutput) { fail++; Console.WriteLine("FAIL tunfp: one edit per covered field kind"); }
        // cost on this host: 3 outputs (the field count printed)
        const int kN = 200000;
        uint sink = 0;
        for (int i = 0; i < 2000; i++) sink += Fp(t);
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < kN; i++) sink += Fp(t);
        sw.Stop();
        double ns = sw.Elapsed.TotalMilliseconds * 1e6 / kN;
        Console.WriteLine("tunfp: " + sFields + " fields (" + AutLiveCore.TunFp.kScalars + " + " + AutLiveCore.TunFp.kPerOutput + " x 3 outputs), "
            + ns.ToString("0") + " ns per fingerprint on this host (.NET Framework), 40 pairs x 2 (request + resume) = "
            + (ns * 80 / 1000).ToString("0.0") + " us per search (sink " + (sink & 1) + ")");
        return fail;
    }
}
