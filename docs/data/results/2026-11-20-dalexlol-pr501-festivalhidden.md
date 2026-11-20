# 2026-11-20: PR #501 `festivalhidden`, imported

By Dalexlol. Contributor data copied from his PR comments (11-19 and 11-20); not rerun here.

Evidence (his machine, natural play, switch on, no off arm in the same load):

| Job | World | Min | Result | looked at / skipped |
|---|---|---|---|---|
| `sl1-play` (2026-11-17) | Starlight Shores | 19.5 | PASS | 7,319 / 44, 0 errors |
| `g1-sts-festival` (2026-11-20, build u1119c) | Starlight Shores | 13.9 | PASS, no crash | 4,921 / 40 |

The 11-19 17-job soak (6 worlds, 493.5 min, all PASS) had no Starlight Shores load: 0 skips there, 0 errors, every call took EA's path. In 122 other game logs with the switch on it looked at 26 to 13,531 calls a game, all non-season items left to EA, 0 errors.

Scope: objects only; Sims are skipped (`(object)this is Sim`), the world lot and room -1 are skipped; household Sims are only affected by no longer being offered an invisible out-of-season object.
