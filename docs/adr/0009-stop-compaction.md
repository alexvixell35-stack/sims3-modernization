# 0009. Compaction work stopped

- **Status:** accepted. Decision by Rodney, 2026-11-07 ("no gains").
- **Context:** the idea was to give address space back by saving and reloading the save in process.
- **Decision:** stop. `bp-save-reload` on master 0.36.189 (Bridgeport after 25 min of play): largest free address space 1326.9 MB before the save, 1282.6 MB after the save and after the in-process menu reload, so nothing was given back. The restart control run was cancelled, so there is no control data. `compaction-verify` is dropped; Compaction stays default off, no code change.
- **Consequences:** memory work continues on leaks and unbounded growth instead.
- **Sources:** PLAN.md "Compaction work STOPPED 2026-11-07"; docs/204.
