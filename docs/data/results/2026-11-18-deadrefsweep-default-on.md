# deadrefsweep default ON (2026-11-18, Rodney)

`kDeadRefSweep` = true in ts3fixes.package.hh-exempt-audit-1118b. Rodney widened it the same evening: deadrefconvo, deadrefcooking, sarcmummynpc, kSpouseUnlink, kRelRemoveSafe, kFamilyInvGhost, kAttractionAlarm are default ON too (one save-matrix bundle, docs/289 §6).

Cost: the 2026-11-03 measurement (`2026-11-03-alexvixell35-stack-deadref-obj-daily.md`, old save, docs/205 §2): sweep ~23 ms per hourly pass after the object-daily change (was ~91 ms), 40 GameObject walks in the run.

Gate: save matrix PASS (queue `hh-exempt-audit-correct-1118.json`, job `bp-hh-exempt-save-matrix`: deadrefsweep + deadrefconvo + ghostreactdeadsim on, then the full matrix of docs/205). A FAIL turns `deadrefsweep` off again. Status: NOT RUN.
