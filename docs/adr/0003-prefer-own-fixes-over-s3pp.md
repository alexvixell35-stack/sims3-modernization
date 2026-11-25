# 0003. Prefer our own fixes over Sims 3 Performance Patch features; no fork

- **Status:** accepted. Decision by Rodney, 2026-10-16.
- **Context:** S3PP (GPLv3) switches each feature in its own ini. Some of its features overlap fixes we build.
- **Decision:** no S3PP fork. Where one of our fixes overlaps an S3PP feature and wins its A/B, the installer turns that S3PP feature off (queue field `s3ppIni`) and ships ours; S3PP features we do not replace stay on. Inventory of the 9 S3PP 2.1 features: keep 4, compatible 2, replace 1 (`LocalizedStringCache`), off 2.
- **Consequences:** the installer needs a conditional ini override; our replacement must read the active string-table layout at run time, because S3PP shrinks the tables.
- **Sources:** PLAN.md "Decision (Rodney, 2026-10-16): prefer our fixes; no S3PP fork"; docs/69 (inventory).
