# [MonoFast] CallPath host bench 2026-11-13

Decision: `[MonoFast] CallPath=1` default ON (Rodney's interpreter/JIT rule 2026-11-13: host bench gain, no penalty, guards hold). Times UTC. Raw: `probe-out/jit-bench/callpath.csv` / `.json` (git-ignored, main checkout).

## Host bench [verified: measurement, host only]

- JIT off: CallPath saves 0.4-2.3 ns per simple call, 5-9 ns on the multi-call rows (cases 16, 17, 21-28); p95 equal or lower on every row.
- JIT on (`Direct=1`): neutral on compiled rows (+0.5 / +1.0 ns, inside noise); interpreted fallthrough still gains.
- Checksums: no `CHECKSUM DIFFERS`.
- Earlier: game verify (`mintcall-verify`) 4/4 exact; host 0 mismatch over 19-21M calls (ts3native 0.36.127).

## Calls per second [verified: counters] and modelled gain [inferred]

Calls/s = `mcBail*` deltas of `mintcall-ab` 10-31 / 600 s.

| world | calls/s | modelled ms/s |
|---|---|---|
| BP | 1.46M | 2.2 |
| IP | 1.53M | 2.3 |
| SV | 1.72M | 2.6 |

Range 1.5-3.5 ms/s [inferred: calls/s x ns saved per call; no in-game timing of the ON default]. The older "-8 ms/s Mono time" (mintcall-ab, 7 runs) is not used: it was never confirmed and exceeds this model.

## Dependencies and guards [verified: source, `native/ts3native/mintcall.cpp`]

- No `[MonoJit] Enabled` dependency: `ApplyMintCall` reads only `[MonoFast] CallPath` / `VerifyEvery`; mintcall.cpp has no monojit reference.
- Install refuses (log `SKIP mintcall`) when EA's call-path bytes (FNV of 5 ranges) or the 4 jump-table entries are not stock (another hook).
- Every non-trivial case bails to EA's handler (new frame slot, transform, native body, cache miss, exception, newobj, yield, ...).
- Kill switch `mcSwitch`: 0 = every call bails to EA. New in this change: a verify-mode mismatch sets it to 0 for the session and logs `mintcall verify: fast path off for the session`. Before, a mismatch only counted (`mcMismatch`). CallPath=1 runs no compare (verify is heavy), so the shipped mode has install-time and per-call bail guards only; the kill switch covers CallPath=2 runs.

## JIT gains ledger

`docs/data/jit-gains.csv` does not exist on master; this file is the record.

## Correctness queue

`tools/harness/queues/callpath-on-1113.json` (prepared, not run): BP / IP / SV verify, then the shipped-ini default job.
