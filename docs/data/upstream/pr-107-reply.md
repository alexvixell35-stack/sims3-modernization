Thanks, the 30-minute probe is a good method and the result ("cold paths never run out") is clear.

We compared it with our own cold give-ups from the `Satb=2` act runs: 7 per run, ops `0xC000` x5 and `0xC001` x2. In Mode R they are all ts3fixes' own worker-only helpers:

- `AutShadowNative.Abort`, `ResetAbort` and `IsWorker`;
- the rain-shelter branch (`RainShelterBranch`, `RainProbe.SeasonsOn` and `TargetOutside`);
- one `Dictionary<,>.get_Count` instance.

None of them is a game scoring method, because the sim's reference run warms every game path first. So your 488 scoring-stack first hits are the go-live problem (no reference run), not today's. We will pre-transform those 7 at load now, and use your list for go-live: bind the closure ICs up front and pre-transform the static closure, without running `.cctor`s.

Your abort-on-cold pre-check, which reads the flags before the work, is better than ours. Ours detects the first-time work after it has started on the worker. We will design it in.

Two changes before merge:

- Master already has docs/208-211. Please rename to `docs/214-autonomy-cold-paths.md` and update the heading, the CLAUDE.md and PLAN.md lines, and the `docs/210` header comments in `tools/autonomy-cold-paths.ts` and `tools/frida/cold-paths.js`.
- Please add `tools/autonomy-cold-paths.ts --self-check` to `tools/selftest-all.ts`.
