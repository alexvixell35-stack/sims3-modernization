# 0007. New behaviour ships default off until measured

- **Status:** accepted.
- **Context:** a fix that helps on one machine can crash or stall on another.
- **Decision:** new behaviour is off by default (`[Section] Enabled=0`, a `[Tunable]` default false) with a runtime switch for A/B, until results from at least two machines show it helps and does not crash. A release enables only features that passed their runs; the release workflow fails if any other native section is enabled.
- **Consequences:** release defaults are decided after runs, one switch list per release (docs/RELEASE.md); pending features sit behind switches.
- **Sources:** CONTRIBUTING.md ("New behaviour off by default"); .github/workflows/build.yml (release job, "Not stable, must be Enabled=0").
