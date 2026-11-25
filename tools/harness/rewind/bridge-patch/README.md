# Bridge rewind mode: patch and build steps

Design, protocol findings and limits: `docs/270-snapshot-rewind-tests.md` section 12. Test-only, never for players.

**Superseded 2026-11-19:** Apex main carries the rewind patch (0015, review fixes) and the 0016 fix (in its 0010). The test
install is now built from our fork's integration branch: `node tools/turbo-compat.ts install-copy --build` (branch and commit
in `FORK` there; notes in `probe-out/apex-latest-switch-1119.md`). This folder and `build.cmd` stay for the old v4.0.6
pair in `game-copy/rewind-bridge-1115`.

The RTX Remix bridge belongs to Apex Radiance (`loinyx/Sims3-ApexRadiance`, `native/remix-bridge/`: NVIDIA's
`dxvk-remix/bridge`, MIT, plus Apex's patches). This folder holds only our patch for it, as the diff Apex's build applies,
and the steps to build and test it. The context lines of the diff are NVIDIA's code under the MIT license. We never change
Apex's installed files; the built pair goes into a copy under `game-copy/` (git-ignored).

| File | What |
|---|---|
| `0015-bridge-rewind-mode.patch` | The patch, as it sits on the local branch `feature/bridge-rewind` of the Apex clone |
| `0016-bridge-render-target-0-is-no-device-reference.patch` | Fix for Apex's patch 0010 (a new device lost its swap chain at the first `AddRef` + `Release`, docs/270 §29); on the same branch, independent of rewind mode |
| `build.cmd` | Clones Apex at the release tag, adds the patch, makes a venv with meson and ninja, runs Apex's `build.cmd` |
| `PR-BODY.md` | Pull request body for Apex's repo, in their template |
| `PR-DRAFT.md` | Title, the `gh pr create --draft` command, and what to fill in before it leaves draft |
| `TEST-GUIDE.md` | Plain steps for Apex's maintainer to try it |

## Steps

1. Build: `tools\harness\rewind\bridge-patch\build.cmd` (about 5 min the first time). Output:
   `game-copy\rewind-bridge-1115\apex-repo\native\remix-bridge\out\d3d9.dll` and `out\.trex\NvRemixBridge.exe`.
2. Install copy: `node tools/turbo-compat.ts install-copy`. It unpacks the latest Apex release into
   `game-copy\rewind-bridge-1115\install\apex-unz`, puts the pair over that copy and appends `rewindMode = True` to both
   `bridge.conf`. It refuses when the latest release differs from the tag in `build.cmd` and `REWIND_BUILT_ON`
   (`tools/turbo-compat.ts`): move both to the new tag, delete `apex-repo`, build again.
3. Test arm: Bin state `apex-rewind` (`node tools/turbo-compat.ts snapshot <dir>`, `state <dir> apex-rewind`, run,
   `restore <dir>`). The chain `probe-out/queue/chain-rewind-smoke-1115.sh` does this around queue `rewind-bridge-1115`.
4. Offline tests, no game: in the Apex clone, `native\remix-bridge\test-rewind.cmd` after its `build.cmd` (the server
   logic on a D3D9 device, and a 32-bit app through the built pair; docs/270 §12.4).
5. Read: rows in `probe-out/rewind/bridge-1115-*.jsonl` (`bridge*` stats and refuse lines), and the server's `[Rewind]`
   lines in `Bin\rtx-remix\logs\bridge64.log` (the chain copies the logs into its snapshot folder).

## Changing the patch

Edit the fetched source in `apex-repo\native\remix-bridge\src\bridge\`, build, then write the diff against the tree with
patches 0001-0014 applied back to `patches\0015-bridge-rewind-mode.patch` (keep its two header lines), copy it here, and
commit both. The patch also holds the two tests (`bridge/test/rtx/unit/test_rewind_*.cpp`) and their meson entries;
run `test-rewind.cmd` before committing. Apex's rules for that repo: its `CONTRIBUTING.md`, `docs/PUBLIC-TEXT-POLICY.md`
(`python tools/public_text_check.py` must pass), and the code style in the fetched source's `AGENTS.md`.
