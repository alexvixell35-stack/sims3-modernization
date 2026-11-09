// [UafCheck] free tags (docs/539 §17, float-5 decisive run (a)): a per-thread marker set while our code makes EA free or
// release objects. uafcheck.cpp stores it (tag + our call site) in each quarantine record and prints it on every
// "uaf: write after free" line, so a 0x40A00000 in a quarantined block says which of our paths freed it (or "none").
// Test diagnostics only: setting the tag is one thread-local write; only [UafCheck] Enabled=1 reads it. Innermost scope wins.
// Included by ts3native.cpp before footprint.cpp (before every tagged site); UafNearLog is defined in uafcheck.cpp.
enum { UT_NONE, UT_P5C_FLUSH, UT_DISC_REPLAY, UT_LOST_REMOVAL, UT_GHOST_SWEEP, UT_LOTRM_FLUSH, UT_THUMBPOSE, UT_ROUTE_JOB,
       UT_MONO_WORKER, UT_N };
static const char* const kUafTagName[UT_N] = {"none", "DestroyPark flush", "Discourage replay", "LostRemovalFix",
                                              "GhostSweep", "LotRemoveGuard flush", "ThumbPose release",
                                              "RouteAsync worker job", "MonoThread worker"};
struct UafTag { DWORD tag, site; };
static __declspec(thread) UafTag t_uafTag;
// manual pair for functions with __try (no object unwinding there)
static inline UafTag UafTagSet(DWORD tag, const void* site) {
    const UafTag prev = t_uafTag;
    t_uafTag.tag = tag, t_uafTag.site = (DWORD)(uintptr_t)site;
    return prev;
}
static inline void UafTagPut(UafTag prev) { t_uafTag = prev; }
struct UafTagScope {
    UafTag prev;
    UafTagScope(DWORD tag, const void* site) : prev(UafTagSet(tag, site)) {}
    ~UafTagScope() { UafTagPut(prev); }
};
static int UafNearLog(DWORD a, const char* what);  // uafcheck.cpp: "float5 near our free" lookups (no-op unless [UafCheck] on)
