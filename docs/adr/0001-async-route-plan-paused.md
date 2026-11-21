# 0001. The first async route plan is paused; only the gated design ships

- **Status:** partly superseded. The gated design (`[RouteAsync]` with `[RouteGate]`, `[Discourage]`) is on by default since release v0.1.4 (README feature table).
- **Context:** `[RouteAsync]` (ts3native 0.5-0.7) ran the whole `IC_Route_Plan` on a worker thread. It crashed three times in game, each root-caused: a yield inside a nested invoke, a host delegate call on the worker, and heap corruption because the goal step runs outside the routing lock on lot-planner state shared by all callers.
- **Decision:** pause the first design and keep its code opt-in. Do not build thread-private A* scratch ("option B"): the per-lot search object is a shared lot-graph cache, not scratch (docs/18). Take the "routing gate" instead: the worker holds the routing lock for the whole job and every unlocked sim-thread entry into the lot planner takes it too.
- **Consequences:** gain is limited to sim-thread work that touches no routing lock; later work is about the waits the gate leaves behind.
- **Sources:** docs/re/dead-ends.md §1 ("what would make it worth revisiting"); docs/18-route-search-off-thread.md; README.md feature table (async route plan row).
