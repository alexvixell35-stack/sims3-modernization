# 0002. No 64-bit resource offload helper

- **Status:** accepted (dropped after the Phase 0 measurement).
- **Context:** docs/08 proposed a 64-bit helper process holding the resource cache, package indexes and decoded resources outside the 32-bit address space, to relieve Error 12.
- **Decision:** do not build it. Phase 0 measured that package indexes are tiny (~3.5 MB for 172 CC packages), packages are not memory-mapped, and the resource cache holds 390-410 MB in Bridgeport whatever the budget (200, 50 or 0 MB), because it is resources in use by the loaded world; only ~10-20 MB is cold. At most ~20 MB of address space to win for an estimated 60-100 h.
- **Consequences:** address-space work goes to leaks and growth (docs/190 memory metrics) and to rendering out of process. Revisit only if a workload shows a large cold share (store repacks in use).
- **Sources:** docs/re/dead-ends.md §7 and §8; docs/08-resource-offload-design.md (Phase 0).
