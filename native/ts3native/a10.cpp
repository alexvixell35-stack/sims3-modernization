// [RouteAsync] Kinds (design A10, docs/87 §5): the Replan-family route ICs as async jobs, like Route.Plan. Included by
// ts3native.cpp after p10.cpp. Off by default (Kinds=0: nothing hooked, today's behaviour).
//
// The three ICs [verified disassembly, Steam 1.67; docs/87 §2]: IC_Route_Replan 0x765490 cdecl(result*, handle) like
// IC_Route_Plan (plus `add [route+0x1F4],1` before its start-point builder 0x7641D0); IC_Route_PlanFromPoint 0x765240 and
// IC_Route_ReplanFromPosition 0x765550 cdecl(result*, handle, x, y, z): the point is three floats by value, so the job
// keeps its own copy and no argument points into managed memory. Each wraps its work in the route's vtable +0x10/+0x14
// AddRef/Release and copies a 0x48-byte RoutePlanResult to result*. Their direct-call closure to Route_PlanCommon (5 hops)
// is a subset of the IC_Route_Plan job's (9 hops), except Route_StartPointsBuild 0x7642C0 and its lot helper 0x74BE50
// (World_LotLookup, 0xB01390, 0xADD800: lot reads the goal step makes in every Plan job) [verified closure]; no managed
// entry before Route_PlanLocked [verified]. So a job runs the whole original IC on the worker, exactly as a Plan job.
//
// Handshake (ts3fixes RouteBackoff.cs ReplanHook / PlanFromPointHook / ReplanFromPointHook): POINT x 3 (kinds 2-3), START
// of the kind (GetOptionHook in ts3native.cpp), READY / CANCEL as for Route.Plan; the C# side then calls the original
// method, whose internal call lands here and takes the stored result (IcCollect: same route, same kind, same point).
// Every rule of the Plan path is the shared code: yield check, M1 refusals, queue, D drains at START, gate waits, H2,
// G10-C, D-X destroy waits, a stale job dropped by the next START of its route or by its own IC.
// New refusal: the route is being followed (Route_IsActive), arActive.
// Mode (arMode, --ab asyncreplan): 0 = shadow, every check runs and START is refused (arShadow), 1 = async.

// docs/670 §24 [RouteAsync] ForeignKindsUnseen: foreign kind STARTs of routes scoped unseen. Cause it answers [verified code,
// s170 / s189 logs]: the sync PlanFromPoint plans logged `entry Route_PlanLocked (no IC)` are foreign STARTs: the follower is a
// boat or another non-Sim planned in its rider's task (EA's houseboat gangway leg and boat legs: `ufplansplit: kind 2 ... sync
// (START refused), why -1, src Boat*`), ts3fixes sends them with bit 61, and the release ForeignKinds=4 has no PlanFromPoint
// bit, so A10StartBody refuses them "Foreign"; kind 2 is hooked only for WatchdogKind2, so ArPass sends the call to the game's
// IC with no SyncIc and no reason line. With the kind's bit here, such a START passes the foreign gate when ts3fixes scoped
// this route unseen right before it (op 11: the Sim follower, or a vehicle's driver; a driverless vehicle by the household
// rule). No answer, household / selected (the player's Go Here), seen NPC, unknown scope and another route's answer stay
// refused "Foreign" and plan synchronously as today; HhExempt is the gate, so [Experimental] HouseholdToo=1 lifts it (the job
// runs the game's whole IC: a worker-computed answer, docs/271). The same scope test as the own-START gate of docs/288 §4
// (ArOwnGateOk: KdScopePeek == 2), which covers only own STARTs. After the gate the job is an ordinary A10 foreign job (af:
// KdA10ForeignMs, Active / NoYield / M1 / queue refusals, no fb cap). Log only: passes / refusals per kind, capped lines.
static volatile LONG g_afUnseenPass[3], g_afUnseenNot[3], g_afUnseenLines;
static bool AfUnseenPass(DWORD handle, int idx) {
    if (idx < 1 || idx > 3 || !(g_afUnseenKinds >> (idx - 1) & 1)) return false;
    if (HhExempt(KdScopePeek(handle) != 2)) { InterlockedIncrement(&g_afUnseenNot[idx - 1]); return false; }
    const LONG n = InterlockedIncrement(&g_afUnseenPass[idx - 1]);
    if ((n & (n - 1)) == 0 && InterlockedIncrement(&g_afUnseenLines) <= 24)  // 1, 2, 4, 8, ... per kind: a capped trail
        Log("route async: ForeignKindsUnseen: foreign kind %d START of unseen route %08lX passes the foreign gate (passes %ld %ld %ld, not unseen "
            "%ld %ld %ld)", idx, handle, g_afUnseenPass[0], g_afUnseenPass[1], g_afUnseenPass[2], g_afUnseenNot[0], g_afUnseenNot[1], g_afUnseenNot[2]);
    return true;
}

// docs/670 §24: a call ArPass sends to the game's IC runs with no SyncIc, so no `sync plan ... reason` line names why it had no
// job (routephases.cpp logs it as `entry Route_PlanLocked (no IC)`). Tally by the refusal its START recorded for this route
// (g_rfbHandle / g_rfbWhy, taken as SyncIc takes it; NoStart = refused at the kind gate or no point) or kRf3None = no START was
// recorded for it (ts3fixes sent none: the task may not yield; or another route's START came in between). Log only, sim thread:
// one line at the 1st, 2nd, 4th, ... pass of each kind, 30 lines a session at most. Returns the reason code (selftest).
static LONG g_arPassWhy[3][kRf3None + 1], g_arPassN[3], g_arPassLines;
static int ArPassNote(int kind, DWORD handle) {
    int why = kRf3None;
    if (g_rfbHandle == handle) why = g_rfbWhy, g_rfbHandle = 0;
    if (kind < 1 || kind > 3 || why < 0 || why > kRf3None) return why;
    LONG* row = g_arPassWhy[kind - 1];
    row[why]++;
    const LONG n = ++g_arPassN[kind - 1];
    if ((n & (n - 1)) != 0 || ++g_arPassLines > 30) return why;
    char by[320] = "";
    size_t at = 0;
    for (int w = 0; w <= kRf3None && at + 40 < sizeof(by); w++) {
        if (!row[w]) continue;
        const int m = _snprintf_s(by + at, sizeof(by) - at, _TRUNCATE, " %s %ld", w == kRf3None ? "NoStartMsg" : w <= RFB_FOREIGN ? kRfbName[w] : "KindsOff", row[w]);
        if (m <= 0) break;
        at += (size_t)m;
    }
    Log("route async: kind %d call of route %08lX runs the game's IC with no job (%s); such calls of this kind by reason:%s", kind, handle,
        why == kRf3None ? "no START recorded for it" : kRfbName[why], by);
    return why;
}

static __declspec(noinline) u8* __cdecl ArReplanHook(u8* out, DWORD handle) {
    const void* ret = _ReturnAddress();
    if (g_p10On) P10Enter(1, (DWORD*)_AddressOfReturnAddress());  // P10 tags it here: its own stub is not installed then
    if (ArPass(1, handle)) {  // docs/670 §24: hooked only for ForeignKindsUnseen, not this route's job
        ArPassNote(1, handle);
        return ((IcPlanFn)g_arOrig[1])(out, handle);
    }
    return IcCollect(1, out, handle, NULL, ret);
}
static __declspec(noinline) u8* __cdecl ArPlanFromPointHook(u8* out, DWORD handle, float x, float y, float z) {
    const void* ret = _ReturnAddress();
    if (g_p10On) P10Enter(2, (DWORD*)_AddressOfReturnAddress());
    const float pt[3] = {x, y, z};
    // docs/250 §7.1 WatchdogKind2: hooked only for the watchdog's own STARTs, every call that is not collecting a job of this
    // route goes to the game's IC directly, as with no detour (no SyncIc: no caps, deadlines or timing on it)
    if (ArWdPass(handle)) {
        InterlockedIncrement(&ts3native_counters.arWdPass);
        ArPassNote(2, handle);
        return ((IcPointFn)g_arOrig[2])(out, handle, x, y, z);
    }
    return IcCollect(2, out, handle, pt, ret);
}
static __declspec(noinline) u8* __cdecl ArReplanFromPositionHook(u8* out, DWORD handle, float x, float y, float z) {
    const void* ret = _ReturnAddress();
    if (g_p10On) P10Enter(3, (DWORD*)_AddressOfReturnAddress());
    const float pt[3] = {x, y, z};
    if (ArPass(3, handle)) {  // docs/670 §24: hooked only for ForeignKindsUnseen
        ArPassNote(3, handle);
        return ((IcPointFn)g_arOrig[3])(out, handle, x, y, z);
    }
    return IcCollect(3, out, handle, pt, ret);
}
static void* const kArHooks[4] = {NULL, (void*)ArReplanHook, (void*)ArPlanFromPointHook, (void*)ArReplanFromPositionHook};
static const int kArBuilt = 7;  // the Kinds bits this build hooks (ts3fixes hooks the same methods)

// [RouteAsync] Kinds = bit 0 Replan, bit 1 PlanFromPoint, bit 2 ReplanFromPosition (7 = all); KindsMode = arMode's start.
// [RouteAsync] ForeignKinds (docs/176, default 0) = the same bits for STARTs from a yielding task that is not the route's Sim
// (vehicle/boat followers, other tasks; ts3fixes sends them with bit 61); ForeignKindsMode = afMode's start (default 1,
// --ab a10foreign). A kind in ForeignKinds only is hooked too, but its own-task STARTs are refused (g_arOwnKinds), so
// Kinds=0 ForeignKinds=4 moves only the foreign ReplanFromPosition plans. Foreign jobs are flagged af (not fb): no
// FbJobCapMs cap, the worker runs the whole original IC, so the answer is the game's own.
// [RouteAsync] WatchdogKind2 (docs/250 §7.1, default 0): PlanFromPoint is hooked too, and an own START of it passes the Kinds
// gate only when tagged as the watchdog's idle check (ts3fixes watchdogasync A1, ScProbeMark bit 10 for the same route; A1
// never picks a household, selected or unknown-scope Sim). With kind 2 in neither Kinds nor ForeignKinds, every other call
// of that IC goes straight to the game (ArWdPass, counted arWdPass).
// [RouteAsync] ForeignKindsUnseen (docs/670 §24, default 0): the ForeignKinds bits for foreign STARTs (bit 61) of routes scoped
// unseen (AfUnseenPass above); a kind in ForeignKinds already starts for every scope. Hooked like the others; ForeignKindsMode
// (afMode) switches it with ForeignKinds. A kind hooked only for it passes every other call straight to the game (ArPass).
// Needs the async path (InstallAsync). Before InstallP10, which then leaves the hooked ICs to these detours.
static void InstallA10(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const int kinds = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"Kinds", 0, ini), 0, 7);
    const int fkinds = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"ForeignKinds", 0, ini), 0, 7);
    const int wd2 = GetPrivateProfileIntW(L"RouteAsync", L"WatchdogKind2", 0, ini) ? 2 : 0;
    const int fukinds = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"ForeignKindsUnseen", 0, ini), 0, 7);  // docs/670 §24
    if (!kinds && !fkinds && !wd2 && !fukinds) { Log("route async A10 off (Kinds=0, ForeignKinds=0)"); return; }
    if (!k.asyncReady || !g_icPlanOrig) { Log("SKIP route async A10: needs the async route path"); return; }
    k.arMode = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"KindsMode", 1, ini), 0, 1);
    k.afMode = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"ForeignKindsMode", 1, ini), 0, 1);
    for (int kind = 1; kind <= 3; kind++) {
        if (!((kinds | fkinds | wd2 | fukinds) & kArBuilt & (1 << (kind - 1)))) continue;
        g_arOrig[kind] = InstallDetour(kP10Name[kind], kP10Ic[kind], kP10Pro[kind], sizeof(kP10Pro[kind]), kArHooks[kind], &g_arOrig[kind]);
        if (g_arOrig[kind]) k.arReady |= 1 << (kind - 1);  // a kind without its detour is refused at START
    }
    g_arOwnKinds = kinds;
    g_arOwnGate = kinds ? 1 : 0;  // docs/288 §4: always with Kinds, no switch (household rule)
    k.afKinds = fkinds & k.arReady;
    g_arWdKind2 = wd2 && (k.arReady & 2) ? 1 : 0;
    g_afUnseenKinds = fukinds & ~fkinds & k.arReady;  // a kind in ForeignKinds already starts for every scope
    Log("route async A10: Kinds=%d KindsMode=%ld ForeignKinds=%d ForeignKindsMode=%ld, hooked mask %ld, WatchdogKind2=%d, own gate %s, "
        "ForeignKindsUnseen=%d (docs/670 section 24)", kinds, k.arMode, fkinds, k.afMode, k.arReady, g_arWdKind2,
        g_arOwnGate ? "unseen routes only, Mode 1 needs H2Ic + RefcountGuard" : "none (Kinds=0)", g_afUnseenKinds);
}
