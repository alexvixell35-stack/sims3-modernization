# Queue tag taken by a longer sibling job (2026-11-04)

Machine: Steam, contributor alexvixell35-stack. Queue combo-local (local), save "1111 kit5".

## What happened

- The queue ran `combo-rtotail-shadow` first, then `combo-rtotail`.
- The second job ran as `combo-local-2026-11-04-combo-rtotail-r2`, although it was the first run of that job that day.
- `wait-ab.ts combo-local-2026-11-04-combo-rtotail` then failed with ENOENT, and a waiter that matched the plain tag missed the job.

## Cause

`tagTaken` (queue.ts) marks a tag as used when any file in `probe-out/profile` or `probe-out/queue` starts with `<tag>-` or `<tag>.`. Every file of `combo-rtotail-shadow` starts with `combo-rtotail-`.

## Fix

- `tagOwns(t, f, siblings)`: a file belongs to tag `t` when it starts with `t-` or `t.`, and it is not a file of a longer sibling tag that extends `t` (the other job ids of the same queue on the same date, their `-rN` reruns included).
- `tagTaken` uses it with the queue's job tags.
- The self-check asserts that a longer sibling's files and its reruns do not take the shorter tag, and that the job's own files still do.
- docs/TESTING.md: one line on profile tags.

## Checks

- `node tools/harness/queue.ts --self-check`: ok.
- `node tools/pr-preflight.ts`: see the PR.
