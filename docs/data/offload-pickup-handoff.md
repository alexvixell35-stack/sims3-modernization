# Offload env answer pickup: handoff (2026-11-14)

Branch `feature/offload-pickup-1114` (off `feature/offload-unseen-apply-1113`). Source only: NOT BUILT, NOT RUN (a timing queue held the machine). Design and cause: docs/208 §11.

## Why

`bp-off3u-smoke` FAILed `autoff_env_applied=0 (want >=1)`: 1213 env answers kept (`autoff_env_ready`), 3 picked up at a Sim's update (`autoff_env_dry`), 3741 updates with no answer (`autoff_env_apply_none`). The scope gate (P1) worked on the 3 it saw. Cause [inferred]: `PickDueSims` ranked by the share of the period passed with no upper bound, so Sims whose env updates had stopped (low LOD runs none, docs/144 §1.1) held all 8 request slots; their answers aged out unasked.

## What changed

| File | Change |
|---|---|
| `mods/ts3fixes/src/AutOffload.cs` | `PickDueSims` skips a Sim later than its period by more than `kDryTicks` (240); `KeepEnv` reuses an empty / used / aged slot before evicting a fresh unused one; `ReadyEnvFor` marks an aged answer used and counts it. Apply path, `EnvFresh`, the `autoffunseen` scope gate and the household / seen exemption are untouched. |
| `mods/ts3agent/src/plugin/Handlers/PatchHandlers.cs` | agent names for slots 985-987 |
| `docs/registry.md` | slots 985-987 (`fixes-985`) |
| `tools/harness/queues/offload-unseen-apply-1113.json` | smoke and both household jobs gate `autoff_env_dry>=1` instead of `autoff_env_applied>=1`; the two 10-min env jobs (`bp-off3u-env`, `ip-off3u-env`) keep `autoff_env_applied>=1` |

New counters (agent names): `autoff_env_pick_overdue` (985), `autoff_env_asked_expired` (986), `autoff_env_never_asked` (987).

## Next build window

1. Merge into the offload branch (or master per the merge plan), then from the worktree: `node tools/setup/build-fixes.ts` (ts3fixes package) and `node tools/harness/build-agent.ts` (agent names). `selftest-all` and `hook-sites --check` before the commit of the build (no AutOffloadCore change, so the autoffload selftest is unchanged).
2. Park the package: copy `build/ts3fixes.package` to `game-copy/ts3fixes.package.offpickup1114`, `node tools/builds.ts add game-copy/ts3fixes.package.offpickup1114 --name ts3fixes.package.offpickup1114 --commit <sha>`.
3. In `offload-unseen-apply-1113.json` (and `offload-unseen-apply-ab-1113.json` if it is still to run) replace `ts3fixes.package.offunseen1113` with `ts3fixes.package.offpickup1114` in `fixesPackage` and `pinReason`. The asi and ini pins stay (`imp1113`).
4. Run `bp-off3u-smoke` first (4 min), then the rest of the queue.

## Expected counters (smoke, 4 min BP, autoffdry on)

| Counter | Before | Expected after |
|---|---|---|
| `autoff_env_ready` | 1213 | similar (hundreds+) |
| `autoff_env_dry` (pickups) | 3 | a large share of ready: well over 10%; < 1% means the cause is elsewhere |
| `autoff_env_apply_none` | 3741 | clearly lower |
| `autoff_env_pick_overdue` | (new) | > 0 (the low-LOD Sims that used to hold the slots); 0 would refute the cause |
| `autoff_env_never_asked` | (new) | well below ready |
| `autoff_env_asked_expired` | (new) | small; large = the 240-tick limit is too short for the batch rate (next step: look at period vs batch interval) |
| `autoff_env_scope_*`, `apply_stale` | 1 / 1 / 1 | grow with pickups |
| `autoff_env_applied` | 0 | > 0 only when an unseen non-household Sim is high LOD at its update; may stay low |

Risk [inferred]: unseen Sims are mostly low LOD and then get no env update at all, so `autoff_env_applied` may stay near 0 even with good pickup. That is why only the 10-min env jobs gate `applied`; if they fail on `applied` alone while `dry` and the scope counters are high, the finding is "env apply under autoffunseen has little to apply", not a pickup bug.

## Status 2026-11-14 (build window)

- Built from this worktree: `build-fixes.ts` + `build-agent.ts` ok; `selftest-all` 228 PASS / 0 FAIL; `hook-sites.ts --check` rc 0.
- Parked: `game-copy/ts3fixes.package.offpickup1114` (sha256 efe9b53ec0e80437aa219561eb040f10d0942cab61516e601f6310a8247f3d61, builds.json entry, commit 8e87fd56), `game-copy/ts3agent.package.offpickup1114` (sha256 b0a5bbf694afb3fc35511749cb047eb9f6176b0fe9b5bdcd2b88c29202ca8238; not in builds.json, install first as the agent package: cycle.ts --restart installs build/ts3agent.package).
- Repinned `offload-unseen-apply-1113.json` and `offload-unseen-apply-ab-1113.json` to ts3fixes.package.offpickup1114 + the agent package (asi/ini pins stay imp1113). Not run.
