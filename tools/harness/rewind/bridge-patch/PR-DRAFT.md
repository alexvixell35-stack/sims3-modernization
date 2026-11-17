# Pull request for loinyx/Sims3-ApexRadiance: how to open it

Not opened, not pushed. Rodney pushes the branch to his fork and opens it as a draft.

- Branch: `feature/bridge-rewind`, seven commits on Apex's `main` (release 4.0.1, d563695). It is in both local clones:
  `game-copy/rewind-bridge-1115/apex-repo` and `game-copy/rewind-bridge-1115/pr/apex-repo` (docs/270 §12.6).
- Title: `tools: add rewind mode to the RTX Remix bridge`
- Body: `PR-BODY.md` in this folder. It follows Apex's template (Summary, Validation, Checklist) and their public text
  policy (third person, no tool names, no clock times); `python tools/public_text_check.py --text-file` passes.
- Plain steps for Loinyx: `TEST-GUIDE.md` (the body folds a short form of it).

```
cd game-copy/rewind-bridge-1115/apex-repo
git remote add fork <URL of the fork of Sims3-ApexRadiance>
git push fork feature/bridge-rewind
gh pr create --draft --repo loinyx/Sims3-ApexRadiance \
  --head <fork owner>:feature/bridge-rewind --base main \
  --title "tools: add rewind mode to the RTX Remix bridge" \
  --body-file <this repo>/tools/harness/rewind/bridge-patch/PR-BODY.md
```

## Before it leaves draft

1. Run `rewind-bridge-1115` on the pair of the branch tip (docs/270 §12.6: the first run used commit 1's pair).
2. In `PR-BODY.md`, Validation, "In game": replace the queued sentence with the results (mode on and idle, the plan's
   numbers, the rewind: applied or refused with the reason) and name what stays unverified.
3. Add the same rows to Apex's `docs/validation/rtx-remix-bridge.md` (section "Rewind mode (patch 0015)") and take the
   line out of its "Open checks", in a further commit `docs: record in-game results of bridge rewind mode`.
4. If the tip changes, update "Tested commit" in the body.
