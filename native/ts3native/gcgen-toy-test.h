// TestGenGcToyHeap ([GC] Generational, docs/290 §8): host-side model of EA's Boehm collector with sticky mark bits.
// It answers docs/290 §6 #2 / #3 before a live run: are EA's sweep, free bits, block stamp and GC_finalize safe when a
// minor cycle skips GC_clear_marks? Nothing here touches the game; included by selftest.cpp (standalone:
// cl /EHsc /O2 /DGCGEN_TOY_MAIN /Tp gcgen-toy-test.h).
//
// Model (one block = one page = 16 slots of one kind; addresses are slot numbers):
//   header: stamp (+0x18), mark bits (+0x1C), free bits (+0x9C), kind (+0x14: 0 pointer-free, 1 normal, 2 uncollectable)
//   alloc (0xE4C1E0): pop the free list, clear the free bit, stamp = GC_gc_no; the mark bit is NOT touched
//   GC_free (0xE4BEB0): clear the body, push on the free list, set the free bit; mark bit and stamp NOT touched
//   marker (0xE4C5F0 ...): never marks a slot whose free bit is set; rescue (state 1, 0xE4D120) pushes marked, non-free
//     slots of blocks whose page is dirty, then the roots; overflow -> rescan every marked, non-free slot (state 5)
//   dirty pages: GetWriteWatch, read + reset once per stopped-mark attempt (0xE4EFB0), 1-3 attempts per cycle, the
//     mutator runs between attempts while the mark continues (incremental)
//   mark end: GC_gc_no++ ; optional mutator window ; GC_finalize (0xE4AE60) ; GC_start_reclaim (0xE502F0)
//   GC_finalize: weak link cleared if target unmarked and block not stamped; finalizable enqueued if unmarked and not
//     stamped, then marked with everything it reaches (java finalization, [0x119483C] = 1)
//   sweep (0xE50160 / 0xE50070): skip a block with stamp == GC_gc_no; marks &= ~free; no mark left and free list not
//     empty -> unlink the block's free-list entries and free the block; else every slot with neither bit goes to the
//     free list (free bit set, body cleared: this write dirties the page). Lazy (on allocation), finished before the
//     next cycle starts (0xE49F10).
//   full cycle: GC_clear_marks (zero marks except kind 2, stamp every block); minor cycle: neither.
// Checks (ground truth uses generation-tagged pointers, so a pointer to a freed-and-reused slot is "stale" and does not
// keep the new object reachable):
//   C1 at mark end every truly reachable object is marked; without stale pointers every conservatively reachable one is
//   C2 the sweep never frees a truly reachable object            C3 a weak link is never cleared while its target is
//   reachable and never left pointing at a swept slot           C4 a finalizer is never queued for a reachable object
//   C5 after a full cycle no dead object is still waiting for its finalizer or holds an uncleared weak link
// Fault injections must be caught: a lost dirty bit, an extra dirty read, a sweep without the stamp skip, never a full.

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

namespace gentoy {

enum { SLOTS = 16, NF = 3, MAXB = 64, NS = SLOTS * MAXB, NR = 12, NOWN = 4 };
enum { K_ATOMIC = 0, K_NORMAL = 1, K_UNCOLL = 2 };

struct Rng {
    uint32_t s;
    uint32_t next() { return s ^= s << 13, s ^= s >> 17, s ^= s << 5, s; }
    int n(int m) { return (int)(next() % (uint32_t)m); }
};
struct Ref { int p; uint32_t g; };  // address + generation of the slot when the pointer was written (g: ground truth only)
struct Blk { bool exists; uint8_t kind; uint32_t stamp; uint16_t marks, freeb; bool dirty, grungy; };
struct Cfg {
    bool stale = false;        // GC_free with pointers left behind (EA / Mono callers do not null referrers)
    bool window = true;        // mutator runs between mark end and GC_finalize
    int fullEvery = 4;         // 0 = every cycle full, <0 = never full
    size_t stackCap = 1u << 20;
    // faults
    int dropDirtyPct = 0;      // a field store fails to dirty its page
    bool extraRead = false;    // a second dirty read whose bits nobody consumes
    bool noStampSkip = false;  // sweep ignores stamp == GC_gc_no
    // EA hazards, present with full cycles too (docs/290 §8.6): the mutator reads a weak link whose target was unmarked at
    // the last mark end: (a) between mark end and GC_finalize, (b) later, when GC_finalize kept the link because the
    // target's block was allocated into after the mark end (stamp test 0xE4AEF3) while the target's referents were swept.
    // Off: the mutator skips such reads, so every error left belongs to the sticky-mark change.
    bool eaWeakHazard = false;
};

struct Heap {
    Cfg cfg;
    Rng r{0x1234567u};
    Blk b[MAXB]{};
    Ref f[NS][NF];
    uint32_t gen[NS]{}, born[NS]{};
    bool alloc[NS]{}, hasFo[NS]{};
    std::vector<int> fl[3], rl[3], stk;
    Ref roots[NR], own[NOWN];
    std::vector<Ref> finNow, fo;
    struct Link { Ref t; bool cleared; };
    std::vector<Link> links;
    uint32_t gcNo = 1;
    bool overflow = false, rtValid = false, marking = false;
    bool rt[NS];
    int errs = 0, sinceFull = 0;
    const char* firstErr = nullptr;
    long nMinor = 0, nFull = 0, nSwept = 0, nBornMarked = 0, nStaleMiss = 0, nFinalized = 0, nCleared = 0, nOverflow = 0,
         nBlocksFreed = 0, nDyingRead = 0;

    Heap() {
        for (auto& o : f) for (auto& x : o) x = {-1, 0};
        for (auto& x : roots) x = {-1, 0};
        for (auto& x : own) x = {-1, 0};
    }
    void err(const char* what) { if (!errs++) firstErr = what; }
    static int bi(int p) { return p / SLOTS; }
    static uint16_t bit(int p) { return (uint16_t)(1u << (p % SLOTS)); }
    bool marked(int p) const { return (b[bi(p)].marks & bit(p)) != 0; }
    bool isFree(int p) const { return (b[bi(p)].freeb & bit(p)) != 0; }
    bool valid(Ref x) const { return x.p >= 0 && alloc[x.p] && gen[x.p] == x.g; }
    void touch() { rtValid = false; }

    // ---- ground truth ----
    void truth() {
        if (rtValid) return;
        memset(rt, 0, sizeof rt);
        std::vector<int> q;
        auto add = [&](Ref x) { if (valid(x) && !rt[x.p]) rt[x.p] = true, q.push_back(x.p); };
        for (auto x : roots) add(x);
        for (auto x : own) add(x);
        for (auto x : finNow) add(x);
        for (int p = 0; p < NS; p++) if (alloc[p] && b[bi(p)].kind == K_UNCOLL) add({p, gen[p]});
        while (!q.empty()) {
            const int p = q.back();
            q.pop_back();
            if (b[bi(p)].kind != K_ATOMIC) for (auto x : f[p]) add(x);
        }
        rtValid = true;
    }

    // ---- allocator ----
    void dead(int p) {  // the collector frees slot p
        truth();
        if (rt[p]) err("C2 sweep freed a reachable object");
        for (auto& l : links) if (!l.cleared && l.t.p == p && l.t.g == gen[p]) err("C3 weak link left dangling");
        if (hasFo[p]) err("C4 finalizable object swept without its finalizer");
        alloc[p] = false, gen[p]++, nSwept++;
        for (auto& x : f[p]) x = {-1, 0};
    }
    void reclaim(int i) {
        Blk& k = b[i];
        if (!cfg.noStampSkip && k.stamp == gcNo) return;  // 0xE500B6
        k.marks &= (uint16_t)~k.freeb;                     // 0xE500DA
        std::vector<int>& l = fl[k.kind];
        if (!k.marks && !l.empty()) {                      // 0xE500E8: empty block, unlink its entries (0xE4FF00), free it
            size_t w = 0;
            for (int p : l) if (bi(p) != i) l[w++] = p;
            l.resize(w);
            for (int p = i * SLOTS; p < (i + 1) * SLOTS; p++) if (alloc[p]) dead(p); else gen[p]++;
            k.exists = false, nBlocksFreed++;
            return;
        }
        for (int p = i * SLOTS; p < (i + 1) * SLOTS; p++)
            if (!marked(p) && !isFree(p)) {
                if (alloc[p]) dead(p);
                k.freeb |= bit(p), l.push_back(p), k.dirty = true;  // link + clear: a write to the page
            }
        if (k.kind == K_UNCOLL) k.marks = 0xFFFF;  // 0xE50059
    }
    void reclaimAll() {
        for (auto& l : rl) { for (int i : l) if (b[i].exists) reclaim(i); l.clear(); }
    }
    int allocObj(int kind) {
        std::vector<int>& l = fl[kind];
        while (l.empty() && !rl[kind].empty()) {  // GC_continue_reclaim 0xE50310
            const int i = rl[kind].back();
            rl[kind].pop_back();
            if (b[i].exists) reclaim(i);
        }
        if (l.empty()) {  // new block: 0xE48FFE.. header, GC_new_hblk free list + free bits
            int i = 0;
            while (i < MAXB && b[i].exists) i++;
            if (i == MAXB) return -1;
            b[i] = {true, (uint8_t)kind, gcNo, (uint16_t)(kind == K_UNCOLL ? 0xFFFF : 0), 0xFFFF, true, false};
            for (int p = (i + 1) * SLOTS - 1; p >= i * SLOTS; p--) l.push_back(p);
        }
        const int p = l.back();
        l.pop_back();
        Blk& k = b[bi(p)];
        k.freeb &= (uint16_t)~bit(p), k.stamp = gcNo;  // 0xE4C1E0
        if (kind == K_UNCOLL) k.marks |= bit(p);
        if (kind != K_ATOMIC) k.dirty = true;
        if (marked(p) && kind != K_UNCOLL) nBornMarked++;
        alloc[p] = true, hasFo[p] = false, born[p] = gcNo;
        touch();
        return p;
    }
    void gcFree(int p) {  // 0xE4BEB0
        for (auto& l : links) if (!l.cleared && l.t.p == p && l.t.g == gen[p]) l.cleared = true;  // owner unregisters
        for (size_t i = 0; i < fo.size(); i++) if (fo[i].p == p) fo[i] = fo.back(), fo.pop_back(), i--;
        hasFo[p] = false;
        for (auto& x : f[p]) x = {-1, 0};
        Blk& k = b[bi(p)];
        k.freeb |= bit(p), k.dirty = true, fl[k.kind].push_back(p);
        alloc[p] = false, gen[p]++;
        touch();
    }
    void store(int h, int i, Ref v) {
        f[h][i] = v;
        if (!(cfg.dropDirtyPct && r.n(100) < cfg.dropDirtyPct)) b[bi(h)].dirty = true;
        touch();
    }
    Ref ref(int p) const { return p < 0 ? Ref{-1, 0} : Ref{p, gen[p]}; }

    // ---- marker ----
    void pushScan(int p) {
        if (b[bi(p)].kind == K_ATOMIC) return;
        if (stk.size() >= cfg.stackCap) { overflow = true; return; }  // entry dropped; state 5 recovers
        stk.push_back(p);
    }
    void tryMark(int p) {
        if (p < 0 || !b[bi(p)].exists || isFree(p) || marked(p)) return;
        b[bi(p)].marks |= bit(p), pushScan(p);
    }
    void pushRoots() {
        for (auto x : roots) tryMark(x.p);
        for (auto x : own) tryMark(x.p);
        for (auto x : finNow) tryMark(x.p);
    }
    void pushMarked(bool dirtyOnly, bool uncollOnly) {  // 0xE4D120 / GC_push_next_marked / state 2
        for (int i = 0; i < MAXB; i++) {
            if (!b[i].exists || (dirtyOnly && !b[i].grungy) || (uncollOnly && b[i].kind != K_UNCOLL)) continue;
            for (int p = i * SLOTS; p < (i + 1) * SLOTS; p++) if (marked(p) && !isFree(p)) pushScan(p);
        }
    }
    bool drain(int budget) {  // true = mark stack empty and no overflow pending
        for (;;) {
            while (!stk.empty()) {
                if (budget-- <= 0) return false;
                const int p = stk.back();
                stk.pop_back();
                for (auto x : f[p]) tryMark(x.p);
            }
            if (!overflow) return true;
            overflow = false, nOverflow++;
            // state 5 with GC_objects_are_marked: rescan every marked object (scanned in place here, so a pass always
            // makes progress on a tiny stack; EA grows the stack instead), then the roots
            for (int p = 0; p < NS; p++)
                if (b[bi(p)].exists && marked(p) && !isFree(p) && b[bi(p)].kind != K_ATOMIC) for (auto x : f[p]) tryMark(x.p);
            pushRoots();
        }
    }
    void readDirty() {
        for (auto& k : b) k.grungy = k.dirty, k.dirty = false;
    }
    void markEndCheck() {
        truth();
        bool c[NS]{};
        std::vector<int> q;
        auto add = [&](int p) { if (p >= 0 && b[bi(p)].exists && !isFree(p) && !c[p]) c[p] = true, q.push_back(p); };
        for (auto x : roots) add(x.p);
        for (auto x : own) add(x.p);
        for (auto x : finNow) add(x.p);
        for (int p = 0; p < NS; p++) if (alloc[p] && b[bi(p)].kind == K_UNCOLL) add(p);
        while (!q.empty()) {
            const int p = q.back();
            q.pop_back();
            if (b[bi(p)].kind != K_ATOMIC) for (auto x : f[p]) add(x.p);
        }
        for (int p = 0; p < NS; p++) {
            if (rt[p] && !marked(p)) err("C1 reachable object unmarked at mark end");
            else if (c[p] && !marked(p)) { nStaleMiss++; if (!cfg.stale) err("C1 minor mark is not a superset of the full mark"); }
        }
    }
    void markFromFo(int p) {  // GC_MARK_FO + mark loop: everything p reaches, not p itself
        if (b[bi(p)].kind != K_ATOMIC) for (auto x : f[p]) tryMark(x.p);
        drain(1 << 30);
    }
    void finalize() {  // 0xE4AE60
        truth();
        for (auto& l : links)
            if (!l.cleared && !marked(l.t.p) && b[bi(l.t.p)].stamp != gcNo) {
                if (valid(l.t) && rt[l.t.p]) err("C3 weak link cleared while its target is reachable");
                l.cleared = true, nCleared++;
            }
        // step 2 (mark from finalizable objects) is empty: Mono registers no-order finalizers, whose mark proc
        // (0xC0FD60) does nothing, so a dead object and the dead objects it points to are queued in the same cycle
        for (size_t i = 0; i < fo.size(); i++) {
            const Ref x = fo[i];
            if (marked(x.p)) continue;
            if (b[bi(x.p)].stamp != gcNo) {
                if (rt[x.p]) err("C4 finalizer queued for a reachable object");
                finNow.push_back(x), hasFo[x.p] = false, nFinalized++;
                fo[i] = fo.back(), fo.pop_back(), i--;
            } else markFromFo(x.p), b[bi(x.p)].marks |= bit(x.p);
        }
        for (auto x : finNow) if (!marked(x.p)) markFromFo(x.p), b[bi(x.p)].marks |= bit(x.p);
        touch();
    }
    template <class M> void cycle(bool minor, int attempts, M&& mutate) {
        reclaimAll();  // 0xE49F10
        bool invalid = false;
        if (!minor) {  // GC_clear_marks 0xE4C270
            for (auto& k : b) if (k.exists) { k.stamp = gcNo; if (k.kind != K_UNCOLL) k.marks = 0; }
            invalid = true;
        }
        minor ? (nMinor++, sinceFull++) : (nFull++, sinceFull = 0);
        marking = true;
        for (int a = 0; a < attempts; a++) {
            readDirty();
            if (cfg.extraRead) mutate(), readDirty();
            if (invalid) pushMarked(false, true), invalid = false;
            else pushMarked(true, false);
            pushRoots();
            if (a + 1 < attempts) while (!drain(8 + r.n(64))) mutate();  // budget spent: world restarts
            else drain(1 << 30);
            if (a + 1 < attempts) mutate();
        }
        markEndCheck();
        marking = false;
        gcNo++;
        if (cfg.window) mutate();
        finalize();
        for (int i = 0; i < MAXB; i++)
            if (b[i].exists && (cfg.noStampSkip || b[i].stamp != gcNo)) rl[b[i].kind].push_back(i);  // 0xE50160
    }
    void policyCycle(int attempts, void (*mut)(Heap&)) {
        const bool minor = cfg.fullEvery < 0 || (cfg.fullEvery > 0 && nFull > 0 && sinceFull < cfg.fullEvery);
        cycle(minor, attempts, [&] { if (mut) mut(*this); });
    }
    int deadWaiting() {  // C5: dead objects whose finalizer / weak link was not processed
        truth();
        int n = 0;
        for (auto x : fo) n += !rt[x.p];
        for (auto& l : links) n += !l.cleared && !(valid(l.t) && rt[l.t.p]);
        return n;
    }

    // ---- random mutator ----
    int walk() {
        Ref x = roots[r.n(NR)];
        if (!valid(x)) return -1;
        for (int d = r.n(6); d > 0; d--) {
            if (b[bi(x.p)].kind == K_ATOMIC) break;
            const Ref y = f[x.p][r.n(NF)];
            if (!valid(y)) break;
            x = y;
        }
        return x.p;
    }
    void op() {
        const int c = r.n(100);
        if (c < 34) {  // allocate and attach
            const int kind = r.n(5) ? K_NORMAL : K_ATOMIC;
            const int p = allocObj(kind);
            if (p < 0) return;
            const int h = walk();
            if (h < 0 || b[bi(h)].kind == K_ATOMIC || r.n(4) == 0) roots[r.n(NR)] = ref(p);
            else store(h, r.n(NF), ref(p));
        } else if (c < 60) {  // pointer store (old -> young included)
            const int h = walk();
            if (h >= 0 && b[bi(h)].kind != K_ATOMIC) store(h, r.n(NF), ref(r.n(3) ? walk() : -1));
        } else if (c < 72) {
            roots[r.n(NR)] = ref(r.n(3) ? -1 : walk());
        } else if (c < 80) {  // owned object (normal or uncollectable cell): allocate, or drop its only reference and GC_free
            Ref& o = own[r.n(NOWN)];
            if (valid(o)) { const int p = o.p; o = {-1, 0}; gcFree(p); }
            else { const int p = allocObj(r.n(3) ? K_NORMAL : K_UNCOLL); o = ref(p); if (p >= 0) store(p, r.n(NF), ref(walk())); }
        } else if (c < 84) {
            const int p = walk();  // explicit free that leaves pointers behind
            if (cfg.stale && p >= 0) gcFree(p);
        } else if (c < 89) {
            const int p = walk();
            if (p < 0) return;
            for (auto& l : links) if (l.cleared) { l = {ref(p), false}; return; }  // reuse a cleared entry
            if (links.size() < 256) links.push_back({ref(p), false});
        } else if (c < 93) {  // weak read
            if (links.empty()) return;
            const Link& l = links[r.n((int)links.size())];
            if (l.cleared) return;
            if (valid(l.t) && !marking && !marked(l.t.p) && born[l.t.p] != gcNo) {  // dying target: EA hazard
                nDyingRead++;
                if (!cfg.eaWeakHazard) return;
            }
            if (!valid(l.t)) { err("C3 weak link read after its target was swept"); return; }
            if (r.n(2)) roots[r.n(NR)] = l.t;
        } else if (c < 97) {
            const int p = walk();
            if (p >= 0 && !hasFo[p] && b[bi(p)].kind != K_UNCOLL) fo.push_back(ref(p)), hasFo[p] = true;
        } else if (!finNow.empty()) {  // run one finalizer; sometimes it resurrects its object
            const Ref x = finNow.back();
            finNow.pop_back();
            if (r.n(5) == 0) roots[r.n(NR)] = x;
        }
        touch();
    }
    static void burst(Heap& h) { for (int i = h.r.n(40); i > 0; i--) h.op(); }
};

static void none(Heap&) {}

struct Tot { long errs, minor, full, swept, born, stale, fin, clr, ovf, blk, dying; const char* first; };

static Tot run(const Cfg& cfg, uint32_t seed, int cycles) {
    Heap* hp = new Heap;
    Heap& h = *hp;
    h.cfg = cfg, h.r.s = seed * 2654435761u + 1;
    for (int i = 0; i < 200; i++) h.op();
    for (int c = 0; c < cycles && !h.errs; c++) {
        for (int i = h.r.n(120); i > 0; i--) h.op();
        h.policyCycle(1 + h.r.n(3), Heap::burst);
    }
    // C5: two full cycles settle resurrection, then nothing dead may still wait (not with stale pointers: they keep
    // dead objects marked, in a full cycle as well)
    if (!h.errs && cfg.fullEvery >= 0 && !cfg.stale) {
        Cfg q = h.cfg;
        h.cfg.fullEvery = 0, h.cfg.window = false;
        for (int i = 0; i < 2; i++) h.finNow.clear(), h.touch(), h.policyCycle(1, nullptr);
        if (h.deadWaiting()) h.err("C5 dead object still waiting after a full cycle");
        h.cfg = q;
    }
    const Tot t{h.errs, h.nMinor, h.nFull, h.nSwept, h.nBornMarked, h.nStaleMiss, h.nFinalized, h.nCleared, h.nOverflow,
                h.nBlocksFreed, h.nDyingRead, h.firstErr};
    delete hp;
    return t;
}

}  // namespace gentoy

#define GT_CHECK(c) do { if (!(c)) { printf("TestGenGcToyHeap FAILED line %d: %s\n", __LINE__, #c); exit(1); } } while (0)

static void TestGenGcToyHeap() {
    using namespace gentoy;
    auto mk = [](Heap& h, int kind = K_NORMAL) { const int p = h.allocObj(kind); GT_CHECK(p >= 0); return p; };
    auto minor = [](Heap& h) { h.cycle(true, 1, [] {}); h.reclaimAll(); };
    auto full = [](Heap& h) { h.cycle(false, 1, [] {}); h.reclaimAll(); };

    {  // 1. old -> young store: dirty page keeps the young object; the same store with the dirty bit lost frees it
        for (int lose = 0; lose < 2; lose++) {
            Heap* h = new Heap;
            h->cfg.window = false;
            const int a = mk(*h);
            h->roots[0] = h->ref(a);
            for (int i = 1, prev = a; i < SLOTS; i++) {  // fill a's block with live objects: no free slot, no sweep write
                const int p = mk(*h);
                h->store(prev, 1, h->ref(p)), prev = p;
            }
            for (int i = 0; i < 40; i++) mk(*h);  // garbage in other blocks
            full(*h), minor(*h);                  // the minor's read resets the dirty bits the allocations left
            GT_CHECK(h->marked(a) && !h->errs);
            const int y = mk(*h);
            GT_CHECK(Heap::bi(y) != Heap::bi(a) && !h->b[Heap::bi(a)].dirty);  // a is old and its page is clean
            h->cfg.dropDirtyPct = lose ? 100 : 0;
            h->store(a, 0, h->ref(y));
            h->cfg.dropDirtyPct = 0;
            minor(*h);
            if (!lose) GT_CHECK(!h->errs && h->alloc[y] && h->marked(y));
            else GT_CHECK(h->errs && strstr(h->firstErr, "C1"));  // caught at mark end, before the sweep frees it
            delete h;
        }
    }
    {  // 2 + 3. young garbage dies in a minor; old garbage survives minors and dies at the full
        Heap* h = new Heap;
        h->cfg.window = false;
        const int a = mk(*h), o = mk(*h);
        h->roots[0] = h->ref(a), h->roots[1] = h->ref(o);
        full(*h);
        const int y = mk(*h);
        h->roots[1] = {-1, 0};  // o is old garbage now
        minor(*h);              // y's block is stamped (allocated into after the mark end): skipped by this sweep
        minor(*h);
        GT_CHECK(!h->alloc[y] && h->alloc[o] && h->marked(o) && !h->errs);
        full(*h);
        GT_CHECK(!h->alloc[o] && h->alloc[a] && !h->errs);
        delete h;
    }
    {  // 4. GC_free of an old object, slot reused before any sweep: born marked, survives minors, dies at the full
        Heap* h = new Heap;
        h->cfg.window = false;
        const int a = mk(*h);
        h->own[0] = h->ref(a);
        full(*h);
        h->own[0] = {-1, 0};
        h->gcFree(a);
        const int n = mk(*h);
        GT_CHECK(n == a && h->marked(n) && h->nBornMarked == 1);
        const int y = mk(*h);
        h->store(n, 0, h->ref(y));  // the born-old object points at a young one; nothing else does
        h->roots[0] = h->ref(n);
        minor(*h), minor(*h);
        GT_CHECK(!h->errs && h->alloc[y] && h->marked(y));
        h->roots[0] = {-1, 0};
        minor(*h), minor(*h);
        GT_CHECK(h->alloc[n] && !h->errs);  // garbage, but old
        full(*h);
        GT_CHECK(!h->alloc[n] && !h->alloc[y] && !h->errs);
        // freed, not reused, swept: the free-bit mask drops the mark, so the next user of the slot is young
        const int c = mk(*h);
        h->own[0] = h->ref(c);
        full(*h);
        h->own[0] = {-1, 0};
        h->gcFree(c);
        GT_CHECK(h->marked(c));
        minor(*h), minor(*h);
        GT_CHECK(!h->marked(c) && h->isFree(c) && !h->errs);
        delete h;
    }
    {  // 5. allocation after the mark end, before GC_finalize / sweep: the stamp protects it; without the skip it is freed
        for (int bug = 0; bug < 2; bug++) {
            Heap* h = new Heap;
            h->cfg.noStampSkip = bug != 0;
            const int a = mk(*h);
            h->roots[0] = h->ref(a);
            full(*h);
            int y = -1;
            h->cycle(true, 1, [&] { if (y < 0) y = h->allocObj(K_NORMAL), h->roots[1] = h->ref(y); });
            h->reclaimAll();
            if (!bug) {
                GT_CHECK(!h->errs && h->alloc[y] && !h->marked(y));
                minor(*h);
                GT_CHECK(!h->errs && h->alloc[y] && h->marked(y));
            } else GT_CHECK(h->errs && strstr(h->firstErr, "C2"));
            delete h;
        }
    }
    {  // 6. mark-stack overflow in a minor: rescan of every marked object, same result
        Heap* h = new Heap;
        h->cfg.window = false, h->cfg.stackCap = 2;
        int prev = mk(*h);
        h->roots[0] = h->ref(prev);
        for (int i = 0; i < 60; i++) {
            const int p = mk(*h);
            h->store(prev, 0, h->ref(p)), h->store(prev, 1, h->ref(mk(*h))), h->store(prev, 2, h->ref(mk(*h)));
            prev = p;
        }
        full(*h);
        const long o1 = h->nOverflow;
        for (int i = 0; i < 30; i++) {
            const int p = mk(*h);
            h->store(prev, 0, h->ref(p)), h->store(prev, 1, h->ref(mk(*h))), h->store(prev, 2, h->ref(mk(*h)));
            prev = p;
        }
        minor(*h);
        GT_CHECK(o1 > 0 && h->nOverflow > o1 && !h->errs && h->alloc[prev] && h->marked(prev));
        delete h;
    }
    {  // 7. finalizer and weak link of an old object: not in a minor, at the next full; young ones in the minor
        Heap* h = new Heap;
        h->cfg.window = false;
        const int o = mk(*h), w = mk(*h);
        h->roots[0] = h->ref(o), h->roots[1] = h->ref(w);
        h->fo.push_back(h->ref(o)), h->hasFo[o] = true;
        h->links.push_back({h->ref(w), false});
        full(*h);
        h->roots[0] = h->roots[1] = {-1, 0};
        const int y = mk(*h), yw = mk(*h);
        h->fo.push_back(h->ref(y)), h->hasFo[y] = true;
        h->links.push_back({h->ref(yw), false});
        minor(*h), minor(*h);
        GT_CHECK(!h->errs && h->nFinalized == 1 && h->finNow[0].p == y && h->links[1].cleared && !h->alloc[yw]);
        GT_CHECK(h->hasFo[o] && !h->links[0].cleared && h->alloc[w] && h->deadWaiting() == 2);
        h->finNow.clear(), h->touch();
        full(*h);
        GT_CHECK(!h->errs && h->nFinalized == 2 && h->finNow[0].p == o && h->alloc[o] && h->links[0].cleared && !h->alloc[w]);
        GT_CHECK(h->deadWaiting() == 0);
        h->finNow.clear(), h->touch();  // finalizer ran, no resurrection
        minor(*h);
        GT_CHECK(h->alloc[o]);  // marked by GC_finalize: old again, freed by the next full only
        full(*h);
        GT_CHECK(!h->alloc[o] && !h->errs);
        delete h;
    }

    // Randomized: all-full baseline (validates the model), sticky minors, overflow, stale frees; then the faults.
    Tot s{};
    auto many = [&](Cfg cfg, int seeds, int cycles) {
        Tot t{};
        for (int i = 1; i <= seeds; i++) {
            const Tot x = run(cfg, (uint32_t)i, cycles);
            if (x.errs && !t.errs) t.first = x.first;
            t.errs += x.errs, t.minor += x.minor, t.full += x.full, t.swept += x.swept, t.born += x.born, t.stale += x.stale;
            t.fin += x.fin, t.clr += x.clr, t.ovf += x.ovf, t.blk += x.blk, t.dying += x.dying;
        }
        return t;
    };
    auto need = [](const char* name, const Tot& x, bool ok) {
        if (ok) return;
        printf("TestGenGcToyHeap FAILED %s: errs %ld (%s) minor %ld full %ld swept %ld born %ld stale %ld fin %ld clr %ld "
               "ovf %ld blk %ld\n", name, x.errs, x.first ? x.first : "-", x.minor, x.full, x.swept, x.born, x.stale, x.fin,
               x.clr, x.ovf, x.blk);
        exit(1);
    };
    Cfg c0;
    c0.fullEvery = 0;
    Tot t = many(c0, 40, 60);
    need("all-full baseline", t, !t.errs && t.full > 2000 && t.swept > 10000 && t.fin > 100 && t.clr > 100);
    Cfg c1;
    t = s = many(c1, 150, 80);
    need("sticky", t, !t.errs && t.minor > 3 * t.full / 2 && t.swept > 20000 && t.born > 0 && t.fin > 100 && t.clr > 100 && t.blk > 0);
    Cfg c2 = c1;
    c2.stackCap = 6;
    t = many(c2, 60, 60);
    need("overflow", t, !t.errs && t.ovf > 1000);
    Cfg c3 = c1;
    c3.stale = true;
    t = many(c3, 100, 80);
    need("stale", t, !t.errs);
    const long staleMiss = t.stale;
    Cfg c4 = c1;
    c4.window = false, c4.fullEvery = 16;
    t = many(c4, 60, 80);
    need("no window, full every 16", t, !t.errs);

    Cfg f1 = c1;  // lost dirty bit: 2% of the stores
    f1.dropDirtyPct = 2;
    const Tot e1 = many(f1, 40, 80);
    need("fault: lost dirty bit", e1, e1.errs > 0);
    Cfg f2 = c1;  // a dirty read whose bits are thrown away
    f2.extraRead = true;
    const Tot e2 = many(f2, 40, 80);
    need("fault: extra dirty read", e2, e2.errs > 0);
    Cfg f3 = c1;
    f3.noStampSkip = true;
    const Tot e3 = many(f3, 20, 40);
    need("fault: no stamp skip", e3, e3.errs > 0);
    Cfg f5 = c0;  // EA's own hazard, full cycles only: a weak read resurrects an object the last mark found dead
    f5.eaWeakHazard = true;
    const Tot e5 = many(f5, 40, 60);
    need("EA hazard: weak read of a dying target", e5, e5.errs > 0 && e5.dying > 0);
    {  // never a full cycle: dead old objects keep waiting for finalizer / weak-link clear (docs/290 §8.5 rule 3)
        Cfg f4 = c1;
        f4.fullEvery = -1;
        long waiting = 0;
        for (uint32_t i = 1; i <= 10; i++) {
            Heap* h = new Heap;
            h->cfg = f4, h->r.s = i * 2654435761u + 1;
            for (int k = 0; k < 200; k++) h->op();
            h->cfg.fullEvery = 0, h->policyCycle(1, nullptr), h->cfg.fullEvery = -1;  // one full, then minors only
            for (int c = 0; c < 40; c++) { for (int k = h->r.n(120); k > 0; k--) h->op(); h->policyCycle(1, Heap::burst); }
            for (auto& x : h->roots) x = {-1, 0};
            h->touch();
            for (int c = 0; c < 3; c++) h->finNow.clear(), h->touch(), h->policyCycle(1, nullptr);
            GT_CHECK(!h->errs);
            waiting += h->deadWaiting();
            delete h;
        }
        GT_CHECK(waiting > 0);
    }
    printf("  gengc toy heap: sticky %ld minor / %ld full cycles, %ld swept, %ld born marked, %ld finalized, %ld links "
           "cleared, %ld blocks freed; stale-pointer misses %ld; faults caught: dirty %ld (%s), read %ld, stamp %ld, EA weak window %ld\n",
           s.minor, s.full, s.swept, s.born, s.fin, s.clr, s.blk, staleMiss, e1.errs, e1.first, e2.errs, e3.errs, e5.errs);
}

#ifdef GCGEN_TOY_MAIN
int main() { TestGenGcToyHeap(); puts("TestGenGcToyHeap OK"); return 0; }
#endif
