Thanks, this is the most useful one of the five. You found a real hole.

Satb has no hook on EA's binder native bodies (`0xE525C0`, binder table `0xD9A860`) or on the static-field helpers `0xD89BF0` / `0xD89CD0`. Their stores are not logged on the sim, and on the worker their loads read live memory. docs/122 assumed the statics these bodies read stay constant during the window. That stopped being true once `Satb=2` jobs started running through the next Simulate.

We re-read a few of your sites in the exe and they match:

- the `sSimTickCnt` stores at `0xD81D39`;
- the byref store in `ElapsedTimeInMinutes` at `0xE0A755`;
- `Curve.Fx` at `0xE0AD21`;
- `InternalSetChar` at `0xDDD68D`.

What we plan, not built yet:

- a cut copy of the two tick statics;
- a sim-side log on `0xD89BF0`;
- wraps for `PriorityQueue.Add/RemoveAt/SetCapacity` and `Hashtable.Rehash`;
- on the worker, a give-up when a binder body gets a pointer the sim logged since the cut;
- a Mode R job with a definition that reads `CurrentTicks`.

`Curve.Fx` is the odd one out: the worker writes shared memory there too.

One change before merge: master already has docs/208-211. Please rename to `docs/212-native-managed-writers.md`, update the `# 208.` heading and change `docs/208` to `docs/212` in your CLAUDE.md and PLAN.md lines. The CSV name is fine.
