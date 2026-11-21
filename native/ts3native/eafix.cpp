// [EaCrashFixes] (0.36.121, docs/67 §26.4 + §27.5, counters v141 ca* / tp*, ledger docs/51 §394): guards for two EA
// lifetime bugs that crashed a contributor's game (issues #98, #99). Both switches default OFF. Included by ts3native.cpp.
//
// ConformActor (#98, crash V; crash I had the same site). Animation controllers (base 0x565A80) keep their actor raw at
// +0x1C. For the route-follow body's ConformToRouteController (vtable 0xFC2068, ctor 0x56E4C0) the actor is an
// AnimationComponent's +8 interface (vtable 0x10433EC): the body's own one ([body+0x124], set from the agent step 0x956A2C:
// component bit [0x11DC484] + 8) or one of [body+0xB4]. Three walkers of the body's controller list (+0x1C) ask the actor
// `[[actor]+0x10](controller)` = 0x93E130 ("does my rig still run this controller's serial?"); false = the controller is
// done and the walker drops it (0x84EA00 and 0x853500 unlink and release it, 0x849570 skips its listener). The actor can
// be destroyed while a controller still names it; the call then goes through a freed vtable [verified disassembly].
// Fix: a set of live AnimationComponents (call-site hooks on its only constructor call 0x93F90D -> 0x93E5C0 and its only
// destructor call 0x942E73 -> 0x941CD0; the dtor leaves 0x10433EC at +8, so the vtable word alone cannot tell a freed
// component), and at the three ask sites `push eax; call CaGuard` in place of the virtual call:
//   actor - 8 in the set              -> the game's own call (unchanged behaviour)
//   actor NULL                         -> false (skip[0])
//   vtable 0x10433EC, not in the set   -> false: a destroyed AnimationComponent (skip[1])
//   vtable unreadable, outside .rdata, or slot +0x10 outside .text -> false: freed memory (skip[2])
//   any other in-image vtable          -> the game's own call (another actor type: caOther counts them)
// "false" is the game's own "controller done" answer, so the walker cleans the controller up the way EA does.
// No game state is written; the guard only reads the actor's first dword under SEH.
//
// ThumbPose (#99, crash W). The Sim thumbnail render 0xD59C10, branch (a) (0xD5B745-0xD5B786), looks up an object into a
// local RefPtr (esp+0x28), copies its raw +0xC (the source pose) into the pose context, then releases that RefPtr
// (`call 0x7460F0` at 0xD5B77A) before the pose is evaluated (`call 0x54C570` at 0xD5B86F). Fix: the release call is
// retargeted to TpDefer (keeps the reference, keyed by the render's frame and thread) and the instruction after the
// evaluation (0xD5B874 `mov ecx,[esp+0xAC]`) becomes a call to TpAfterStub, which releases it there (same thread, same
// release function). Branches (b) and (c) hold no reference at all and stay as they are [verified disassembly].
//
// ThumbPoseBones (#99, crash W; 2026-11-05). The first crash W dump showed branch (a) with its object still alive after the
// release (refcount 1), so the fault is not a released pose: the source pose has the model's own bone count (42) and the
// modifier 0x54E630 indexes it with the destination rig's bone indices (222-224). Fix: at the three stores of the source
// pose, a pose holder with fewer bones than the destination rig gives 0, which the modifier already skips (see below).
//
// GeomBoneIndex (0.36.149, docs/67 §28, crash X). The GEOM resource (vtable 0xFE32C0, "CAS/GeometryResource") keeps its
// bone hashes in a vector at +0x58/+0x5C; slot +0x54 (0x5C6E00) returns bones[i] with no bounds check, slot +0x50
// (0x5C6DF0) the count. The Sim builder's skin pass 0x5D1010 asks slot +0x54 for each of a vertex's four blend indices
// (0x5D1631) and looks the hash up in the rig's bone map; a miss takes EA's own path for a bone the rig lacks (0x5D165B).
// A mesh whose blend index is past its own bone list reads past the vector and crashed at 0x5C6E07 [verified disassembly].
// Fix: the vtable slot points to GbGetBone, which answers 0 for such an index, so the lookup misses the way an unknown
// bone does. Indices inside the list get the game's answer.
//
// GeomNoMaterial (0.36.149, issue #80, docs/67 §29). The Sim builder's per-part step 0x5DDB30 asks the part's GEOM for its
// embedded material (vtable slot +0x58 = 0x580420, `mov eax,[ecx+28h]`) and reads the shader hash at [material+4] on every
// path (0x5DDC5A when the part joins an existing group, 0x5DDFBD when it starts one) with no null check. A CAS VPXY LOD
// entry that names a GEOM without one (custom content: a morph mesh with shader 0) crashed there [verified: two dumps +
// disassembly]. The step already skips a part whose mesh pointer is null or tagged (0x5DDB5D / 0x5DDB67 -> 0x5DE0E2,
// returns 1, the caller 0x5DE4CA ignores it). Fix: that check (0x5DDB63) becomes a jump to GnStub, which also takes the
// skip for a GEOM whose slot +0x58 is the game's getter and whose material is null; every other part continues at 0x5DDB6D.
//
// MintDivGuard (0.36.149, issue #114, docs/213 §4 / §8, counters v144 md*). The mint handlers of div.i4 (opcode 0x127,
// 0xE55B4C) and rem.i4 (0x143, 0xE55C0F) check the divisor for 0 only, then `cdq; idiv`: INT_MIN / -1 and INT_MIN % -1
// raise the CPU's divide error, which nothing in the interpreter catches (the sim thread froze in TS3W's own filter in a
// live repro) [verified disassembly + emulation + one game session]. Fix: both jump-table entries point to stubs that
// pass every other case to the game's handler. INT_MIN / -1 goes to the game's OverflowException stub 0xE57AA5 (the one the
// *.ovf opcodes use; it records this instruction as the frame ip), as .NET throws. INT_MIN % -1 gives 0, the exact result.
//
// LotLevelPastEnd (next version, docs/67 §40, no counters). The lot's level Group getter 0xAB70B0 clamps the object's
// level below by the lot's lowest level and never above. A slotted object re-attached across a lot change still carries
// its old lot's level; on a lot with fewer levels the getter returns the word past its Group table, and Realm_MoveShape
// 0x682480 then moves a shape into it (Bridgeport AVs at 0x6824D0 / 0x6824F3 / 0x8621EA) [verified: logs + disassembly].
// Fix: the vtable slot points to LlGroup, which answers the lot's top Group for such a level; in-range asks are unchanged.
//
// EA build only (TS3N_EA_PORT, ts3addr.h): ported from hotfix/v0.2.10-ea169, not in the Steam asi; the EA addresses are
// table rows checked on the decrypted exe (docs/data/ea169-overrides.csv).
// SimNoTexture (0.36.152a, docs/67 §34, crash Z). SimService_Process 0x5EBC50 (request state 11) looks up the request's two
// composite textures in the builder's table (key 0x1F/4 and 0x1F/8) and reads the first one's width at 0x5EBE31 with no null
// check; the second is checked. The texture is missing when its create in state 9 failed (call 0x5CFB7C -> 0x619C80 returned
// false; that path stores nothing) [verified disassembly]. 0x5EBE38 `ja 0x5EBED4` is EA's own path for a texture too wide
// to composite, and the no-recipe paths reach the same target with a null first result. Fix: the load and compare (0x5EBE2D)
// become a jump to SnStub, which takes that path for a missing texture. Nothing after it reads the first result: the mark
// 0x5CEA60 ignores its pointer argument and finds no table entry. The Sim gets no composite from this build.
//
// SimNoTextureAtlas (off, docs/67 §34.6). 0x5E0720 on CAS::SimBuilder (runs with UseCompression and CacheAtlases, script
// ThumbnailHelper for a household leaving CAS or stored to the Library) makes the same two lookups and reads the first at
// 0x5E082A with no null check. A failed create (EA 0x5E277F -> 0x5D0BA0) stores nothing [verified disassembly]. Fix: the load
// at 0x5E077D jumps to SaStub, which for a missing texture takes EA's own no-recipe jump to 0x5E0872, before the composite
// starts. Nothing after that target reads the first texture: the mark at 0x5E098C ignores its pointer argument.

static const DWORD CA_ASK_SITES[3] = {TS3A(0x0084EABD), TS3A(0x00849588), TS3A(0x00853521)};
// mov ecx,[eax+1Ch]; mov edx,[ecx]; push eax; mov eax,[edx+10h]; call eax   (eax = controller)
static const u8 kCaAsk[11] = {0x8B, 0x48, 0x1C, 0x8B, 0x11, 0x50, 0x8B, 0x42, 0x10, 0xFF, 0xD0};
static const DWORD CA_CTOR_SITE = TS3A(0x0093F90D), CA_CTOR = TS3A(0x0093E5C0);  // AnimationComponent ctor (thiscall, owner; ret 4)
static const DWORD CA_DTOR_SITE = TS3A(0x00942E73), CA_DTOR = TS3A(0x00941CD0);  // AnimationComponent dtor (thiscall, no args)
static const DWORD CA_ANIM_VT8 = TS3A(0x010433EC);                          // AnimationComponent +8 interface vtable
static DWORD g_ecaRdLo = TS3A(0x00F95000), g_ecaRdHi = TS3A(0x01146000);            // .rdata (vtables); selftest: its own range
static DWORD g_ecaTxLo = TS3A(0x00401000), g_ecaTxHi = TS3A(0x00F95000);            // .text

typedef void*(__fastcall* CaCtorFn)(u8* comp, void* edx, void* owner);
typedef void(__fastcall* CaDtorFn)(u8* comp);
typedef bool(__fastcall* CaAskFn)(u8* actor, void* edx, u8* ctl);
static CaCtorFn g_ecaCtor = (CaCtorFn)(uintptr_t)CA_CTOR;
static CaDtorFn g_ecaDtor = (CaDtorFn)(uintptr_t)CA_DTOR;
static volatile LONG g_ecaLogged;

// Live set: open addressing, linear probe, 1 = tombstone. Full (more than half live) = tracking stops for the session and
// the guard calls the game for every actor it cannot prove dead by its vtable word (caOverflow).
static const DWORD CA_CAP = 1u << 17, CA_TOMB = 1;
static DWORD* g_ecaSet;
static DWORD g_ecaUsed, g_ecaLive;  // used = live + tombstones
static SRWLOCK g_ecaLock = SRWLOCK_INIT;
static volatile bool g_ecaFull;

static inline DWORD CaSlot(DWORD p) { return ((p >> 3) * 0x9E3779B1u) >> 15; }  // 17 bits

static bool CaHas(DWORD p) {
    AcquireSRWLockShared(&g_ecaLock);
    bool hit = false;
    for (DWORD i = CaSlot(p), n = 0; n < CA_CAP; i = (i + 1) & (CA_CAP - 1), n++) {
        const DWORD k = g_ecaSet[i];
        if (k == p) { hit = true; break; }
        if (!k) break;
    }
    ReleaseSRWLockShared(&g_ecaLock);
    return hit;
}

static void CaInsertLocked(DWORD* set, DWORD p) {
    DWORD i = CaSlot(p);
    while (set[i] > CA_TOMB) i = (i + 1) & (CA_CAP - 1);
    if (!set[i]) g_ecaUsed++;
    set[i] = p;
}

static void CaAdd(DWORD p) {
    if (g_ecaFull) return;
    AcquireSRWLockExclusive(&g_ecaLock);
    for (DWORD i = CaSlot(p), n = 0; n < CA_CAP; i = (i + 1) & (CA_CAP - 1), n++) {  // a reused address that missed its dtor
        if (g_ecaSet[i] == p) { ReleaseSRWLockExclusive(&g_ecaLock); return; }
        if (!g_ecaSet[i]) break;
    }
    if (g_ecaLive + 1 > CA_CAP / 2) {
        g_ecaFull = true, ts3native_counters.caOverflow = 1;
        ReleaseSRWLockExclusive(&g_ecaLock);
        Log("ea fix: ConformActor: more than %lu live AnimationComponents, tracking stopped (unknown actors get the game's call)", CA_CAP / 2);
        return;
    }
    if (g_ecaUsed + 1 > CA_CAP / 4 * 3) {  // rebuild without tombstones (rare: only after many destroys)
        DWORD* fresh = (DWORD*)VirtualAlloc(NULL, CA_CAP * 4, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (fresh) {
            DWORD* old = g_ecaSet;
            g_ecaSet = fresh, g_ecaUsed = 0;
            for (DWORD i = 0; i < CA_CAP; i++)
                if (old[i] > CA_TOMB) CaInsertLocked(fresh, old[i]);
            VirtualFree(old, 0, MEM_RELEASE);
        }
    }
    CaInsertLocked(g_ecaSet, p);
    const LONG live = (LONG)++g_ecaLive;
    ts3native_counters.caLive = live;
    if (live > ts3native_counters.caMaxLive) ts3native_counters.caMaxLive = live;
    ReleaseSRWLockExclusive(&g_ecaLock);
}

static void CaDel(DWORD p) {
    if (g_ecaFull) return;
    AcquireSRWLockExclusive(&g_ecaLock);
    for (DWORD i = CaSlot(p), n = 0; n < CA_CAP; i = (i + 1) & (CA_CAP - 1), n++) {
        if (g_ecaSet[i] == p) { g_ecaSet[i] = CA_TOMB, ts3native_counters.caLive = (LONG)--g_ecaLive; break; }
        if (!g_ecaSet[i]) break;
    }
    ReleaseSRWLockExclusive(&g_ecaLock);
}

static void* __fastcall CaCtorHook(u8* comp, void*, void* owner) {
    void* r = g_ecaCtor(comp, NULL, owner);
    CaAdd((DWORD)(uintptr_t)comp);
    return r;
}

static void __fastcall CaDtorHook(u8* comp) {
    CaDel((DWORD)(uintptr_t)comp);  // before the body: from here on the component counts as gone
    g_ecaDtor(comp);
}

// 0 = call the game (live, or a type we cannot judge); 1-3 = skip reason (index + 1 into caSkip).
static int CaDead(u8* a, DWORD* vtOut) {
    *vtOut = 0;
    if (!a) return 1;
    if (!g_ecaFull && CaHas((DWORD)(uintptr_t)a - 8)) return 0;
    DWORD vt = 0, fn = 0;
    __try { vt = *(const volatile DWORD*)a; } __except (EXCEPTION_EXECUTE_HANDLER) { return 3; }
    *vtOut = vt;
    if (vt == CA_ANIM_VT8) return g_ecaFull ? 0 : 2;
    if (vt < g_ecaRdLo || vt >= g_ecaRdHi || (vt & 3)) return 3;
    __try { fn = ((const volatile DWORD*)(uintptr_t)vt)[4]; } __except (EXCEPTION_EXECUTE_HANDLER) { return 3; }
    if (fn < g_ecaTxLo || fn >= g_ecaTxHi) return 3;
    ts3native_counters.caOther++;  // sim thread; a lost count under a race is fine
    return 0;
}

// Called by the patched ask sites in place of `[[ctl+1Ch]]+10h`(ctl); stdcall pops ctl like the thiscall did.
static bool __stdcall CaGuard(u8* ctl) {
    u8* a = *(u8* const*)(ctl + 0x1C);  // the controller itself is alive: the walker's list holds a reference
    DWORD vt;
    const int why = CaDead(a, &vt);
    if (!why) return ((CaAskFn)(uintptr_t)(*(const DWORD* const*)a)[4])(a, NULL, ctl);  // the game's own call
    InterlockedIncrement(&ts3native_counters.caSkip[why - 1]);
    if (InterlockedIncrement(&g_ecaLogged) <= 8) {
        static const char* const kWhy[3] = {"null actor", "AnimationComponent destroyed", "vtable unreadable or outside the exe"};
        DWORD cvt = 0, serial = 0;
        __try { cvt = *(const DWORD*)ctl, serial = *(const DWORD*)(ctl + 0x20); } __except (EXCEPTION_EXECUTE_HANDLER) {}
        Log("ea fix: ConformActor: controller %p (vtable 0x%08lX, serial %lu) skipped: actor %p (vtable word 0x%08lX): %s",
            ctl, cvt, serial, a, vt, kWhy[why - 1]);
    }
    return false;
}

// Ask site i at `at`: 11 original bytes -> push eax; call CaGuard; 5-byte nop. Returns false (nothing written) on other bytes.
static bool CaPatchAsk(u8* at) {
    if (!Matches(at, kCaAsk, sizeof(kCaAsk))) return false;
    u8 b[11] = {0x50, 0xE8, 0, 0, 0, 0, 0x0F, 0x1F, 0x44, 0x00, 0x00};
    *(LONG*)(b + 2) = (LONG)((uintptr_t)&CaGuard - ((uintptr_t)at + 6));
    return Write(at, b, sizeof(b));
}

static bool ApplyConformActor(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"ConformActor", 0, ini)) return false;
    // all or nothing: the guard without the live set would call every live AnimationComponent dead
    bool ok = FpCallOk(CA_CTOR_SITE, CA_CTOR) && FpCallOk(CA_DTOR_SITE, CA_DTOR);
    for (DWORD s : CA_ASK_SITES) ok = ok && Matches((const void*)(uintptr_t)s, kCaAsk, sizeof(kCaAsk));
    if (!ok) { Log("SKIP ea fix: ConformActor: a site's bytes differ (another mod or a probe build owns it): not installed"); return false; }
    g_ecaSet = (DWORD*)VirtualAlloc(NULL, CA_CAP * 4, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!g_ecaSet) { Log("FAIL ea fix: ConformActor: no memory for the live set"); return false; }
    // the set hooks first: no AnimationComponent exists yet (we run before the exe's entry point)
    if (!FpRedirect(CA_CTOR_SITE, (const void*)&CaCtorHook) || !FpRedirect(CA_DTOR_SITE, (const void*)&CaDtorHook)) {
        Log("FAIL ea fix: ConformActor: cannot write the constructor / destructor call sites");
        return false;
    }
    int n = 0;
    for (DWORD s : CA_ASK_SITES) n += CaPatchAsk((u8*)(uintptr_t)s);
    ts3native_counters.caOn = n;
    Log("OK   ea fix: ConformActor (docs/67 §26.4): live AnimationComponent set + %d of 3 controller ask sites guarded", n);
    return true;  // keep the log open for the skip lines
}

// ---- ThumbPose ----
static const DWORD TP_DEFER_SITE = TS3A(0x00D5B77A), TP_RELEASE = TS3A(0x007460F0);  // call RefPtr_Release (ecx = &RefPtr)
static const DWORD TP_AFTER_SITE = TS3A(0x00D5B874);                           // mov ecx,[esp+0ACh] after the evaluation
static const u8 kTpAfter[7] = {0x8B, 0x8C, 0x24, 0xAC, 0x00, 0x00, 0x00};
typedef void(__fastcall* TpReleaseFn)(u8** ref);
static TpReleaseFn g_tpRelease = (TpReleaseFn)(uintptr_t)TP_RELEASE;

// One deferred reference per render frame in flight: {thread, frame esp, object}. The render does not nest between the
// two sites (only pose/pool calls run there), so one slot per thread is enough; 8 cover the main and sim threads.
struct TpSlot { volatile LONG tid; DWORD frame; u8* obj; };
static TpSlot g_tp[8];

static void TpReleaseNow(u8* obj) {
    __try { if (*(volatile LONG*)(obj + 4) == 1) InterlockedIncrement(&ts3native_counters.tpLastRef); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    u8* tmp = obj;
    const UafTag uafPrev = UafTagSet(UT_THUMBPOSE, _ReturnAddress());  // [UafCheck] (manual pair: __try above)
    g_tpRelease(&tmp);
    UafTagPut(uafPrev);
}

// Replaces `call 0x7460F0` at 0xD5B77A: ecx = &RefPtr local at frame+0x28. Keeps the reference instead of releasing it.
static void __fastcall TpDefer(u8** ref) {
    u8* obj = *ref;
    if (!obj) return;  // release of an empty RefPtr does nothing either
    const LONG tid = (LONG)GetCurrentThreadId();
    const DWORD frame = (DWORD)(uintptr_t)ref - 0x28;
    for (TpSlot& s : g_tp)
        if (s.tid == tid && s.frame == frame && s.obj) TpReleaseNow(s.obj), s.obj = NULL;  // stale (never seen: same frame again)
    for (TpSlot& s : g_tp) {
        if (s.tid == tid && !s.obj) { s.frame = frame, s.obj = obj; InterlockedIncrement(&ts3native_counters.tpHeld); return; }
    }
    for (TpSlot& s : g_tp) {
        if (InterlockedCompareExchange(&s.tid, tid, 0) == 0) { s.frame = frame, s.obj = obj; InterlockedIncrement(&ts3native_counters.tpHeld); return; }
    }
    InterlockedIncrement(&ts3native_counters.tpNoSlot);
    g_tpRelease(ref);  // vanilla order
}

// After the evaluation: release this frame's kept reference (none when branch (b) or (c) ran).
static void __stdcall TpAfter(DWORD frame) {
    const LONG tid = (LONG)GetCurrentThreadId();
    for (TpSlot& s : g_tp)
        if (s.tid == tid && s.frame == frame && s.obj) {
            u8* obj = s.obj;
            s.obj = NULL;
            TpReleaseNow(obj);
            return;
        }
}

// Replaces `mov ecx,[esp+0ACh]` (7 bytes) at 0xD5B874 with `call TpAfterStub; 2-byte nop`. Every register is kept and
// the moved instruction runs last; flags are dead there (push edi; call 0x573330 follow).
static __declspec(naked) void TpAfterStub() {
    __asm {
        pushad
        lea eax, [esp + 36]  // the render's esp: 32 bytes of pushad + our return address
        push eax
        call TpAfter
        popad
        mov ecx, [esp + 4 + 0xAC]
        ret
    }
}

static bool TpPatch(u8* defer, u8* after) {
    u8 call[5] = {0xE8};
    *(LONG*)(call + 1) = (LONG)(TP_RELEASE - ((uintptr_t)defer + 5));
    if (!Matches(defer, call, 5) || !Matches(after, kTpAfter, sizeof(kTpAfter))) return false;
    u8 b[7] = {0xE8, 0, 0, 0, 0, 0x66, 0x90};
    *(LONG*)(b + 1) = (LONG)((uintptr_t)&TpAfterStub - ((uintptr_t)after + 5));
    if (!Write(after, b, sizeof(b))) return false;  // release point first: a deferral is never left without it
    *(LONG*)(call + 1) = (LONG)((uintptr_t)&TpDefer - ((uintptr_t)defer + 5));
    return Write(defer, call, 5);
}

static bool ApplyThumbPose(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"ThumbPose", 0, ini)) return false;
    if (!TpPatch((u8*)(uintptr_t)TP_DEFER_SITE, (u8*)(uintptr_t)TP_AFTER_SITE)) {
        Log("SKIP ea fix: ThumbPose: bytes at 0x%08X / 0x%08X differ: not installed", TP_DEFER_SITE, TP_AFTER_SITE);
        return false;
    }
    ts3native_counters.tpOn = 1;
    Log("OK   ea fix: ThumbPose (docs/67 §27.5): thumbnail branch (a) keeps its object reference until the source pose is evaluated");
    return false;
}

// ---- ThumbPoseBones ----
// Crash W dump (2026-11-04, docs/67 §27.6): the source pose comes from a pose holder (vtable 0xFC2D30, ctor 0x574750: +8 bone
// count, +0xC count x 48 bytes) built for the model's own rig, and the modifier 0x54E630 reads it at the destination rig's
// bone indices (rootWorld / L_footWorld / R_footWorld, 222-224 in the dump; 42-bone holder) with no size check. At the three
// stores of the source pose ([esp+0x45C] = context +0x4C) a holder with fewer bones than the destination rig ([esp+0x7C],
// bone vector +0x2C / +0x30 of 112-byte entries, as 0x575650) gives 0: the modifier skips a null source pose (0x54E754), and
// branch (c) already stores 0 when the model has no holder [verified disassembly].
static const DWORD TB_SITE_B = TS3A(0x00D5B708), TB_SITE_A = TS3A(0x00D5B768), TB_SITE_C = TS3A(0x00D5B7A1), TB_HOLDER_VT = TS3A(0x00FC2D30);
// b: mov ecx,[eax+0Ch]; mov esi,[esp+0A8h]; mov [esp+45Ch],ecx   a: mov eax,[esp+28h]; mov ecx,[eax+0Ch]; mov [esp+45Ch],ecx
// c: mov eax,[eax+0Ch]; jmp +2 (to the store at 0xD5B7A8)
static const u8 kTbB[17] = {0x8B, 0x48, 0x0C, 0x8B, 0xB4, 0x24, 0xA8, 0x00, 0x00, 0x00, 0x89, 0x8C, 0x24, 0x5C, 0x04, 0x00, 0x00};
static const u8 kTbA[14] = {0x8B, 0x44, 0x24, 0x28, 0x8B, 0x48, 0x0C, 0x89, 0x8C, 0x24, 0x5C, 0x04, 0x00, 0x00};
static const u8 kTbC[5] = {0x8B, 0x40, 0x0C, 0xEB, 0x02};
static DWORD g_tbBackC = TB_SITE_C + 7;  // the store; selftest: its landing pad
static volatile LONG g_tbSeen, g_tbDropped;

// The source pose the render may use: the holder's +0xC, or 0 when its bone list is shorter than the destination rig's.
static const u8* __stdcall TbPose(const u8* holder, const u8* rig, DWORD branch) {
    const u8* pose = NULL;
    __try {
        pose = *(const u8* const*)(holder + 0xC);
        if (*(const DWORD*)holder != TB_HOLDER_VT || !rig) return pose;  // another holder type: the game's value
        const DWORD n = *(const DWORD*)(holder + 8), rn = (*(const DWORD*)(rig + 0x30) - *(const DWORD*)(rig + 0x2C)) / 112;
        const LONG seen = InterlockedIncrement(&g_tbSeen);
        if (n >= rn) return pose;
        const LONG k = InterlockedIncrement(&g_tbDropped);
        if (k <= 8 || (k & 1023) == 0)
            Log("ea fix: ThumbPoseBones: branch %c: source pose %lu bones, destination rig %lu: not used (%ld of %ld)", (char)('a' + branch), n, rn, k, seen);
        return NULL;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }  // an unreadable holder faulted at the game's own load
}

// pushad frame: edi +0, esi +4, ebp +8, esp +12, ebx +16, edx +20, ecx +24, eax +28. Called (b, a): render esp = esp + 36.
static __declspec(naked) void TbStubB() {  // eax = holder; out: ecx = the stored pose, esi = [esp+0A8h]
    __asm {
        pushad
        push 1
        push dword ptr [esp + 40 + 0x7C]
        push eax
        call TbPose
        mov [esp + 36 + 0x45C], eax
        mov [esp + 24], eax
        popad
        mov esi, [esp + 4 + 0xA8]
        ret
    }
}
static __declspec(naked) void TbStubA() {  // holder = [esp+28h]; out: eax = holder, ecx = the stored pose
    __asm {
        pushad
        push 0
        push dword ptr [esp + 40 + 0x7C]
        push dword ptr [esp + 44 + 0x28]
        call TbPose
        mov [esp + 36 + 0x45C], eax
        mov [esp + 24], eax
        mov eax, [esp + 36 + 0x28]
        mov [esp + 28], eax
        popad
        ret
    }
}
static __declspec(naked) void TbStubC() {  // jumped to: render esp = esp + 32 after pushad; eax = holder, out eax = pose
    __asm {
        pushad
        push 2
        push dword ptr [esp + 36 + 0x7C]
        push eax
        call TbPose
        mov [esp + 28], eax
        popad
        jmp dword ptr [g_tbBackC]
    }
}

static bool TbPatch(u8* b, u8* a, u8* c) {
    if (!Matches(b, kTbB, sizeof(kTbB)) || !Matches(a, kTbA, sizeof(kTbA)) || !Matches(c, kTbC, sizeof(kTbC))) return false;
    u8 pb[17], pa[14], pc[5] = {0xE9};
    memset(pb, 0x90, sizeof(pb)), memset(pa, 0x90, sizeof(pa));
    pb[0] = pa[0] = 0xE8;
    *(LONG*)(pb + 1) = (LONG)((uintptr_t)&TbStubB - ((uintptr_t)b + 5));
    *(LONG*)(pa + 1) = (LONG)((uintptr_t)&TbStubA - ((uintptr_t)a + 5));
    *(LONG*)(pc + 1) = (LONG)((uintptr_t)&TbStubC - ((uintptr_t)c + 5));
    return Write(b, pb, sizeof(pb)) && Write(a, pa, sizeof(pa)) && Write(c, pc, sizeof(pc));  // DllMain: no render runs yet
}

static bool ApplyThumbPoseBones(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"ThumbPoseBones", 0, ini)) return false;
    if (!TbPatch((u8*)(uintptr_t)TB_SITE_B, (u8*)(uintptr_t)TB_SITE_A, (u8*)(uintptr_t)TB_SITE_C)) {
        Log("SKIP ea fix: ThumbPoseBones: bytes at 0x%08X / 0x%08X / 0x%08X differ: not installed", TB_SITE_B, TB_SITE_A, TB_SITE_C);
        return false;
    }
    Log("OK   ea fix: ThumbPoseBones (docs/67 §27.6): a thumbnail source pose with fewer bones than the destination rig is not used");
    return true;  // keep the log open for the drop lines
}

// ---- GeomBoneIndex ----
static const DWORD GB_SLOT = TS3A(0x00FE3314), GB_GET = TS3A(0x005C6E00), GB_COUNT = TS3A(0x005C6DF0);  // vtable 0xFE32C0 +0x54, +0x50
// mov eax,[ecx+58h]; mov ecx,[esp+4]; mov eax,[eax+ecx*4]; ret 4
static const u8 kGbGet[13] = {0x8B, 0x41, 0x58, 0x8B, 0x4C, 0x24, 0x04, 0x8B, 0x04, 0x88, 0xC2, 0x04, 0x00};
// mov eax,[ecx+5Ch]; sub eax,[ecx+58h]; sar eax,2; ret
static const u8 kGbCount[10] = {0x8B, 0x41, 0x5C, 0x2B, 0x41, 0x58, 0xC1, 0xF8, 0x02, 0xC3};
static volatile LONG g_gbBad;

// The resource key in the resource base (vtable 0xFB2C04): instance (64-bit) at +8, type at +0x10, group at +0x14 [verified:
// a logged key matched a GEOM in the player's packages, docs/67 §28.4]. Printed as type:group:instance, as package tools show it.
static void GeomKey(char (&out)[40], const u8* geom) {
    const DWORD* f = (const DWORD*)(geom + 8);
    sprintf_s(out, "%08lX:%08lX:%08lX%08lX", f[2], f[3], f[1], f[0]);
}

static DWORD __fastcall GbGetBone(u8* geom, void*, DWORD i) {
    const DWORD* b = *(const DWORD* const*)(geom + 0x58);
    const DWORD n = (DWORD)(*(const DWORD* const*)(geom + 0x5C) - b);
    if (i < n) return b[i];
    if (InterlockedIncrement(&g_gbBad) <= 8) {
        char key[40];
        GeomKey(key, geom);
        Log("ea fix: GeomBoneIndex: GEOM %p (key %s) asked for bone %lu of %lu (caller 0x%08X): answered 0, the rig's missing-bone path",
            geom, key, i, n, (DWORD)(uintptr_t)_ReturnAddress());
    }
    return 0;
}

// slot = the vtable entry; checks the getter it names and the count getter (the vector layout) first.
static bool GbPatch(DWORD* slot, const u8* count) {
    if (!Readable(slot, 4) || !Matches((const void*)(uintptr_t)*slot, kGbGet, sizeof(kGbGet)) ||
        !Matches(count, kGbCount, sizeof(kGbCount)))
        return false;
    const DWORD hook = (DWORD)(uintptr_t)&GbGetBone;
    return Write(slot, &hook, 4);
}

static bool ApplyGeomBoneIndex(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"GeomBoneIndex", 0, ini)) return false;
    DWORD* slot = (DWORD*)(uintptr_t)GB_SLOT;
    if (!Readable(slot, 4) || *slot != GB_GET || !GbPatch(slot, (const u8*)(uintptr_t)GB_COUNT)) {
        Log("SKIP ea fix: GeomBoneIndex: vtable slot 0x%08X or the getter bytes differ: not installed", GB_SLOT);
        return false;
    }
    Log("OK   ea fix: GeomBoneIndex (docs/67 §28): GEOM bone-by-index getter answers 0 past the mesh's bone list");
    return true;  // keep the log open for the skip lines
}

// ---- LotLevelPastEnd ----
// docs/67 §40 (Bridgeport: AV at 0x6824D0 / 0x6824F3 / 0x8621EA under Transform_AttachToSlot 0x95A360). The lot's "physics
// Group for this object's level" getter 0xAB70B0 (Lot vtable 0x1065268 +0x6C; only reference: the slot) takes the object's level
// ([level component +0x18], 0x7FFFFFFF = 0, +1 when [+0x2C] bit 5), clamps it below by the lot's lowest level [lot+0x17C] and
// indexes the lot's EASTL deque of Groups (+0x370) with no upper bound. An object that keeps the level of the lot it comes from
// (level 5 of a high-rise) and is attached to a parent on a lot with fewer levels gets whatever lies past the deque: Realm_MoveShape
// then reads or writes through it. The hook replays the index (reads only); in range: the game's getter, unchanged; past the end:
// the lot's top Group (EA's own clamp, mirrored), or 0 (= the realm's root Group, EA's answer for an object with no level) when
// the lot has no Group at all.
static const DWORD LL_SLOT = TS3A(0x010652D4), LL_GET = TS3A(0x00AB70B0);
static const u8 kLlHead[15] = {0x56, 0x8B, 0xF1, 0x8B, 0x4C, 0x24, 0x08, 0x85, 0xC9, 0x0F, 0x84, 0x9A, 0x00, 0x00, 0x00};
static const u8 kLlLow[6] = {0x3B, 0x86, 0x7C, 0x01, 0x00, 0x00};                                       // +0x4D cmp eax,[esi+17Ch]
static const u8 kLlDeq[12] = {0x8B, 0x8E, 0x7C, 0x03, 0x00, 0x00, 0x8B, 0x86, 0x78, 0x03, 0x00, 0x00};  // +0x66 first, cur
static const u8 kLlBlk[6] = {0x8B, 0xB6, 0x84, 0x03, 0x00, 0x00};                                       // +0x79 block table
typedef DWORD(__fastcall* LlFn)(u8* lot, void*, u8* obj);
static LlFn g_llGet = (LlFn)(uintptr_t)LL_GET;      // selftest: its own
static DWORD g_llIdLevel = TS3A(0x011ECF08);        // address of the level component's mask bit (selftest: its own)
static volatile LONG g_llHits;

static LONG LlLevels(const u8* lot) {  // the deque's size, as 0xAB6608 computes it
    const DWORD* q = (const DWORD*)(lot + 0x378);  // begin: cur, first, last, node; end: cur, first, last, node
    return (((LONG)(q[7] - q[3]) >> 2) - 1) * 64 + ((LONG)(q[4] - q[5]) >> 2) + ((LONG)(q[2] - q[0]) >> 2);
}
static DWORD __fastcall LlGroup(u8* lot, void* edx, u8* obj) {
    const DWORD id = *(const DWORD*)(uintptr_t)g_llIdLevel, m = obj ? *(const DWORD*)(obj + 0x2C) : 0;
    if (!(m & id)) return g_llGet(lot, edx, obj);  // no object / no level component: EA answers 0
    DWORD s = 0;
    for (DWORD b = m & (id - 1); b; b &= b - 1) s++;  // QueryComponent 0x935770: the slot = mask bits below the id's
    const u8* lc = *(const u8* const*)(obj + 0x34 + s * 4);
    if (!lc) return g_llGet(lot, edx, obj);
    const LONG raw = *(const LONG*)(lc + 0x18), lo = *(const LONG*)(lot + 0x17C), n = LlLevels(lot);
    LONG lv = raw == 0x7FFFFFFF ? 0 : raw;
    if (*(const DWORD*)(lc + 0x2C) & 0x20) lv++;
    const DWORD i = (DWORD)(lv < lo ? lo : lv) - (DWORD)lo;
    if (n > 0 && i < (DWORD)n) return g_llGet(lot, edx, obj);
    DWORD g = 0;
    if (n > 0) {
        const DWORD* q = (const DWORD*)(lot + 0x378);
        const DWORD idx = (DWORD)((LONG)(q[0] - q[1]) >> 2) + (DWORD)n - 1;
        g = (*(const DWORD* const* const*)(lot + 0x384))[idx >> 6][idx & 63];
    }
    if (InterlockedIncrement(&g_llHits) <= 16) {
        // the script frames name who re-parents the object; the native caller only says "attach by slot name"
        char st[440];
        if (FbStackText(st, sizeof(st), 10) <= 0) strcpy_s(st, "(no script frames)");
        Log("ea fix: LotLevelPastEnd: lot %p id %08lX%08lX has %ld level Group(s) from level %ld, object %p id %08lX%08lX (lot %p) "
            "asks for level %ld (index %ld, caller 0x%08X): answered the top Group 0x%08lX instead of the word past the table; "
            "script stack: %s",
            lot, *(const DWORD*)(lot + 0x4C), *(const DWORD*)(lot + 0x48), n, lo, obj, *(const DWORD*)(obj + 0xC), *(const DWORD*)(obj + 8),
            *(void* const*)(obj + 0x30), lv, (LONG)i, (DWORD)(uintptr_t)_ReturnAddress(), g, st);
    }
    return g;
}

// slot = the vtable entry; checks the getter it names (entry, lowest-level clamp, deque reads) first.
static bool LlPatch(DWORD* slot) {
    if (!Readable(slot, 4)) return false;
    const u8* f = (const u8*)(uintptr_t)*slot;
    if (!Matches(f, kLlHead, sizeof(kLlHead)) || !Matches(f + 0x4D, kLlLow, sizeof(kLlLow)) ||
        !Matches(f + 0x66, kLlDeq, sizeof(kLlDeq)) || !Matches(f + 0x79, kLlBlk, sizeof(kLlBlk)))
        return false;
    const DWORD hook = (DWORD)(uintptr_t)&LlGroup;
    return Write(slot, &hook, 4);
}

static bool ApplyLotLevelPastEnd(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"LotLevelPastEnd", 0, ini)) return false;
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP ea fix LotLevelPastEnd ([EaCrashFixes] LotLevelPastEnd): no EA 1.69 address, docs/232 §8"); return false; }
#endif
    DWORD* slot = (DWORD*)(uintptr_t)LL_SLOT;
    if (!Readable(slot, 4) || *slot != LL_GET || !LlPatch(slot)) {
        Log("SKIP ea fix: LotLevelPastEnd: vtable slot 0x%08X or the getter bytes differ: not installed", LL_SLOT);
        return false;
    }
    Log("OK   ea fix: LotLevelPastEnd (docs/67 §40): the lot's level Group getter 0xAB70B0 answers the top Group past the lot's level table");
    return true;  // keep the log open for the hit lines
}

// ---- GeomNoMaterial ----
static const DWORD GN_SITE = TS3A(0x005DDB63), GN_SLOT = TS3A(0x00FE3318);                          // check in 0x5DDB30; GEOM vtable +0x58
static const DWORD GN_SKIP = TS3A(0x005DE0E2), GN_CONT = TS3A(0x005DDB6D), GN_GET = TS3A(0x00580420);     // skip tail, next instruction, getter
static const u8 kGnSite[10] = {0x8B, 0x03, 0x85, 0xC0, 0x0F, 0x84, 0x75, 0x05, 0x00, 0x00};  // mov eax,[ebx]; test eax,eax; je 0x5DE0E2
static const u8 kGnGet[4] = {0x8B, 0x41, 0x28, 0xC3};                                     // mov eax,[ecx+28h]; ret
static DWORD g_gnSkip = GN_SKIP, g_gnCont = GN_CONT, g_gnGet = GN_GET;  // stub targets and the getter (selftest swaps them)
static volatile LONG g_gnBad;

static void __stdcall GnHit(u8* geom, DWORD lod) {
    if (InterlockedIncrement(&g_gnBad) > 8) return;
    char key[40];
    GeomKey(key, geom);
    Log("ea fix: GeomNoMaterial: GEOM %p (key %s, builder LOD %lu) has no embedded material: part skipped, EA's path for a part "
        "without a mesh", geom, key, lod);
}

// In place of the 10 bytes at 0x5DDB63: ebx = &part.mesh, esi = the builder (LOD at +0x1A0). eax/ecx/edx and the flags are
// dead at both targets (0x5DDB6D reloads all three; the skip tail pops and sets al).
static __declspec(naked) void GnStub() {
    __asm {
        mov eax, [ebx]
        test eax, eax
        je skip                       // the game's own null check
        mov ecx, [eax]
        mov edx, g_gnGet
        cmp [ecx + 0x58], edx
        jne cont                      // another type, or the slot is someone else's: the game's path
        cmp dword ptr [eax + 0x28], 0
        jne cont
        pushad
        push dword ptr [esi + 0x1A0]
        push eax
        call GnHit
        popad
    skip:
        jmp dword ptr [g_gnSkip]
    cont:
        jmp dword ptr [g_gnCont]
    }
}

// site = the 10 bytes at 0x5DDB63, slot = GEOM vtable +0x58: both must be EA's, and the slot must name EA's getter.
static bool GnPatch(u8* site, const DWORD* slot) {
    if (!Matches(site, kGnSite, sizeof(kGnSite)) || !Readable(slot, 4) || *slot != g_gnGet ||
        !Matches((const void*)(uintptr_t)g_gnGet, kGnGet, sizeof(kGnGet)))
        return false;
    u8 b[10] = {0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90, 0x90, 0x90};
    *(LONG*)(b + 1) = (LONG)((uintptr_t)&GnStub - ((uintptr_t)site + 5));
    return Write(site, b, sizeof(b));  // DllMain: no Sim is built yet
}

static bool ApplyGeomNoMaterial(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"GeomNoMaterial", 0, ini)) return false;
    if (!GnPatch((u8*)(uintptr_t)GN_SITE, (const DWORD*)(uintptr_t)GN_SLOT)) {
        Log("SKIP ea fix: GeomNoMaterial: bytes at 0x%08X, vtable slot 0x%08X or the getter differ: not installed", GN_SITE, GN_SLOT);
        return false;
    }
    Log("OK   ea fix: GeomNoMaterial (issue #80, docs/67 §29): a CAS mesh without an embedded material is skipped, not read");
    return true;  // keep the log open for the hit lines
}

// ---- MintDivGuard ----
static const DWORD MD_TABLE = TS3A(0x00E59880);                    // mint jump table (opcode * 4), in .text
static const DWORD MD_DIV = TS3A(0x00E55B4C), MD_REM = TS3A(0x00E55C0F);  // entries 0x127 / 0x143
static const DWORD MD_OVF = TS3A(0x00E57AA5), MD_NEXT = TS3A(0x00E544D5);  // OverflowException stub, dispatch head
static const u8 kMdHead[6] = {0x83, 0x7E, 0xF8, 0x00, 0x0F, 0x84};  // cmp dword [esi-8],0; je <DivideByZero stub>
static DWORD g_mdDiv = MD_DIV, g_mdRem = MD_REM, g_mdOvf = MD_OVF, g_mdNext = MD_NEXT;  // stub jump targets (selftest swaps them)
static volatile LONG g_mdLogged;

static void __stdcall MdHit(int rem) {
    InterlockedIncrement(rem ? &ts3native_counters.mdRemMinus1 : &ts3native_counters.mdDivOverflow);
    if (InterlockedIncrement(&g_mdLogged) > 8) return;
    char st[440];
    if (FbStackText(st, sizeof(st), 8) <= 0) strcpy_s(st, "unreadable");
    Log("ea fix: MintDivGuard: %s: %s; script stack: %s", rem ? "rem.i4 INT_MIN % -1" : "div.i4 INT_MIN / -1",
        rem ? "result 0" : "OverflowException", st);
}

// The handler convention: esi = eval stack top (divisor [esi-8], dividend [esi-0x10], 8-byte slots), edi = this
// instruction, [ebp-4] = the frame's saved ip. Only the INT_MIN / -1 pair leaves the game's path; flags are dead here.
static __declspec(naked) void MdDivStub() {
    __asm {
        cmp dword ptr [esi - 8], -1
        jne game
        cmp dword ptr [esi - 0x10], 0x80000000
        jne game
        pushad
        push 0
        call MdHit
        popad
        jmp dword ptr [g_mdOvf]
    game:
        jmp dword ptr [g_mdDiv]
    }
}

static __declspec(naked) void MdRemStub() {
    __asm {
        cmp dword ptr [esi - 8], -1
        jne game
        cmp dword ptr [esi - 0x10], 0x80000000
        jne game
        pushad
        push 1
        call MdHit
        popad
        add esi, -8                   // as the game's handler: pop the divisor, result over the dividend, next instruction
        mov dword ptr [esi - 8], 0
        add edi, 2
        mov dword ptr [ebp - 4], edi
        jmp dword ptr [g_mdNext]
    game:
        jmp dword ptr [g_mdRem]
    }
}

// table = the jump table; both entries must still name the game's handlers, and both handlers start as EA's.
static bool MdPatch(DWORD* table) {
    DWORD* d = table + 0x127;
    DWORD* r = table + 0x143;
    if (!Readable(d, 4) || !Readable(r, 4) || *d != g_mdDiv || *r != g_mdRem ||
        !Matches((const void*)(uintptr_t)g_mdDiv, kMdHead, sizeof(kMdHead)) || !Matches((const void*)(uintptr_t)g_mdRem, kMdHead, sizeof(kMdHead)))
        return false;
    const DWORD dv = (DWORD)(uintptr_t)&MdDivStub, rv = (DWORD)(uintptr_t)&MdRemStub;
    return Write(d, &dv, 4) && Write(r, &rv, 4);  // aligned 4-byte stores; no script runs yet (DllMain)
}

static bool ApplyMintDivGuard(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"MintDivGuard", 0, ini)) return false;
    if (!MdPatch((DWORD*)(uintptr_t)MD_TABLE)) {
        Log("SKIP ea fix: MintDivGuard: jump-table entries 0x127 / 0x143 or the handler bytes differ: not installed");
        return false;
    }
    ts3native_counters.mdOn = 1;
    Log("OK   ea fix: MintDivGuard (issue #114, docs/213 §8): div.i4 / rem.i4 INT_MIN by -1 no longer fault");
    return true;  // keep the log open for the hit lines
}

#ifdef TS3N_EA_PORT
// ---- SimNoTexture ----
static const DWORD SN_SITE = TS3A(0x005EBE2D), SN_CONT = TS3A(0x005EBE38), SN_SKIP = TS3A(0x005EBED4);  // load + cmp, the `ja`, its target
static const DWORD SN_CREATE_SITE = TS3A(0x005CFB7C), SN_CREATE = TS3A(0x00619C80);  // state 9's texture create (thiscall, 6 args, bool)
static const u8 kSnSite[11] = {0x8B, 0x54, 0x24, 0x74, 0x81, 0x7A, 0x14, 0x00, 0x08, 0x00, 0x00};  // mov edx,[esp+74h]; cmp dword [edx+14h],800h
static DWORD g_snCont = SN_CONT, g_snSkip = SN_SKIP;  // stub targets (selftest swaps them)
static volatile LONG g_snBad;

static void __stdcall SnHit(const u8* req, DWORD second) {
    const LONG n = InterlockedIncrement(&g_snBad);
    if (n > 8) return;
    MEMORYSTATUSEX m = {sizeof(m)};
    GlobalMemoryStatusEx(&m);  // the create failure is inferred to be memory: this number is the evidence
    Log("ea fix: SimNoTexture #%ld: request %p (size tier %lu, second texture %s): first texture missing, composite skipped; "
        "%lu MB address space free", n, req, *(const DWORD*)(req + 8), second ? "present" : "missing", (DWORD)(m.ullAvailVirtual >> 20));
}

// In place of the 11 bytes at 0x5EBE2D, by jump: esi = the request, ebp = the second texture. edx and the flags are dead at
// the skip target (0x5EBED4 cmp ebp,ebx); on the game's path the flags reach the `ja` through the jmp.
static __declspec(naked) void SnStub() {
    __asm {
        mov edx, [esp + 0x74]
        test edx, edx
        je miss
        cmp dword ptr [edx + 0x14], 0x800
        jmp dword ptr [g_snCont]
    miss:
        pushad
        push ebp
        push esi
        call SnHit
        popad
        jmp dword ptr [g_snSkip]
    }
}

static bool SnPatch(u8* site) {
    if (!Matches(site, kSnSite, sizeof(kSnSite))) return false;
    u8 b[11] = {0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90};
    *(LONG*)(b + 1) = (LONG)((uintptr_t)&SnStub - ((uintptr_t)site + 5));
    return Write(site, b, sizeof(b));  // DllMain: no Sim is built yet
}

// Repro (test key, independent of the guard, so the off arm crashes): every Nth create in state 9 answers false after the
// game's call; the caller then releases the good texture as it does on a real failure.
typedef bool(__fastcall* SnCreateFn)(void* img, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
static SnCreateFn g_snCreate = (SnCreateFn)(uintptr_t)SN_CREATE;
static LONG g_snEvery, g_snCreates;
static bool __fastcall SnCreateRepro(void* img, void*, DWORD a, DWORD b, DWORD c, DWORD d, DWORD e, DWORD f) {
    const bool ok = g_snCreate(img, NULL, a, b, c, d, e, f);
    return ok && InterlockedIncrement(&g_snCreates) % g_snEvery != 0;
}

static bool ApplySimNoTexture(const wchar_t* ini) {
    g_snEvery = (LONG)GetPrivateProfileIntW(L"EaCrashFixes", L"SimNoTextureRepro", 0, ini);
    if (g_snEvery > 0) {
        if (FpCallOk(SN_CREATE_SITE, SN_CREATE) && FpRedirect(SN_CREATE_SITE, (const void*)&SnCreateRepro))
            Log("OK   ea fix: SimNoTextureRepro (TEST KEY): every create number %ld of a Sim composite texture fails", g_snEvery);
        else Log("SKIP ea fix: SimNoTextureRepro: call at 0x%08X differs: not installed", SN_CREATE_SITE);
    }
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"SimNoTexture", 0, ini)) return g_snEvery > 0;
    if (!SnPatch((u8*)(uintptr_t)SN_SITE)) {
        Log("SKIP ea fix: SimNoTexture: bytes at 0x%08X differ: not installed", SN_SITE);
        return g_snEvery > 0;
    }
    Log("OK   ea fix: SimNoTexture (docs/67 §34): a Sim request whose composite texture is missing skips the composite");
    return true;  // keep the log open for the hit lines
}

// ---- SimNoTextureAtlas ----
static const DWORD SA_SITE = TS3A(0x005E077D), SA_CONT = TS3A(0x005E0783), SA_SKIP = TS3A(0x005E0872);  // load, next instruction, EA's no-recipe target
static const u8 kSaSite[6] = {0x8B, 0x96, 0xA8, 0x00, 0x00, 0x00};  // mov edx,[esi+0A8h]
static DWORD g_saCont = SA_CONT, g_saSkip = SA_SKIP;  // stub targets (selftest swaps them)
static volatile LONG g_saBad;

static void __stdcall SaHit(const u8* builder, DWORD second) {
    const LONG n = InterlockedIncrement(&g_saBad);
    if (n > 8) return;
    MEMORYSTATUSEX m = {sizeof(m)};
    GlobalMemoryStatusEx(&m);
    Log("ea fix: SimNoTextureAtlas #%ld: builder %p (second texture %s): first texture missing, atlas composite skipped; "
        "%lu MB address space free", n, builder, second ? "present" : "missing", (DWORD)(m.ullAvailVirtual >> 20));
}

// In place of the 6 bytes at 0x5E077D, by jump: esi = the builder, ebx = the second texture, [esp+18h] = the first. eax, ecx,
// edx and the flags are dead at the skip target (0x5E0872 cmp ebx,edi), where edi is still 0.
static __declspec(naked) void SaStub() {
    __asm {
        cmp dword ptr [esp + 0x18], 0
        je miss
        mov edx, [esi + 0xA8]
        jmp dword ptr [g_saCont]
    miss:
        pushad
        push ebx
        push esi
        call SaHit
        popad
        jmp dword ptr [g_saSkip]
    }
}

static bool SaPatch(u8* site) {
    if (!Matches(site, kSaSite, sizeof(kSaSite))) return false;
    u8 b[6] = {0xE9, 0, 0, 0, 0, 0x90};
    *(LONG*)(b + 1) = (LONG)((uintptr_t)&SaStub - ((uintptr_t)site + 5));
    return Write(site, b, sizeof(b));  // DllMain: no Sim is built yet
}

static bool ApplySimNoTextureAtlas(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"SimNoTextureAtlas", 0, ini)) return false;
    if (!SaPatch((u8*)(uintptr_t)SA_SITE)) {
        Log("SKIP ea fix: SimNoTextureAtlas: bytes at 0x%08X differ: not installed", SA_SITE);
        return false;
    }
    Log("OK   ea fix: SimNoTextureAtlas (docs/67 §34.6): a household atlas build whose composite texture is missing skips the composite");
    return true;
}
#endif

// ---- SlaveUnlink + DeadSlaveTrip (2026-11-18, docs/143 §8.11, ledger I-212-16) ----
// A route with slaves (BoatWaterScooter.IsSlaveRoute adds its Driver; other boats, strollers, led horses alike) reaches
// native as Route.Start(..., slave ids, ...); the master agent's entity (agent+8) keeps raw slave entity pointers: count
// +0xC, vector +0x10..+0x14, flag byte +0x20, written only by SetSlaves 0x83A650 thiscall(part; arr, count, flag) ret 0xC,
// whose only setting caller is 0x955809 (the other four pass 0,0,0) [verified]. The agent destructor 0x9562F0 ends in the
// entity base destructor 0x839E60 (call at 0x956426, its only caller besides the abstract base's own) and neither removes the
// dying entity from a master's list [verified], so a slave destroyed while its master's list still names it leaves a
// dangling pointer: the master's next ObstacleAvoidance calls slot +0x18 of the base vtable 0x1022E78 = the purecall thunk
// 0xF7E8AC (crash at 0x58D996 from 0x85F1A8, 2026-10-21 / 11-17 / 11-18 on Isla Paradiso).
// SlaveUnlink: record each list at 0x955809; when a listed entity is destroyed, take it off its master's list before the
// destructor runs (the last one through the game's own SetSlaves(0,0,0), what EA does at route end, 0x858BAE / 0x84B3F1;
// SetSlaves(0,..) only resets end, count and flag, no calls). Lists whose slaves live on are unchanged. DeadSlaveTrip (test queues only): at the four avoidance reads of slave 0 (0x847490, 0x847C57, 0x85E88B,
// 0x85F18F; each tests the result for null) a slave whose vtable slot +0x18 is the purecall thunk is logged and answered null.
typedef void(__fastcall* SlSetFn)(u8* part, void* edx, u8** arr, DWORD n, DWORD flag);
typedef void(__fastcall* SlDtorFn)(u8* part, void* edx);
typedef u8*(__fastcall* SlGetFn)(u8* part, void* edx, int i);
static const DWORD SL_SET = TS3A(0x0083A650), SL_SET_SITE = TS3A(0x00955809), SL_DTOR = TS3A(0x00839E60),
                   SL_DTOR_SITE = TS3A(0x00956426), SL_GET = TS3A(0x00839CA0), SL_PURECALL = TS3A(0x00F7E8AC);
static const DWORD kSlTripSites[4] = {TS3A(0x00847490), TS3A(0x00847C57), TS3A(0x0085E88B), TS3A(0x0085F18F)};
static SlSetFn g_slSet = (SlSetFn)(uintptr_t)SL_SET;  // selftest swaps the three
static SlDtorFn g_slDtor = (SlDtorFn)(uintptr_t)SL_DTOR;
static SlGetFn g_slGet = (SlGetFn)(uintptr_t)SL_GET;
// Own export block (counters layout unchanged, docs/registry.md): switches, lists recorded, masters cleared, table full,
// tripwire hits.
struct SlaveLinkCounters { volatile LONG unlinkOn, tripOn, sets, unlinks, overflow, tripHits; };
extern "C" __declspec(dllexport) SlaveLinkCounters ts3native_slavelink = {0, 0, 0, 0, 0, 0};
struct SlPair { u8* master; u8* slave; DWORD mPartVt, mLo, mHi, mVt, sLo, sHi, sVt; };
enum { SL_CAP = 64 };
static SlPair g_slPairs[SL_CAP];
static volatile LONG g_slBusy;

static void SlOwner(const u8* part, DWORD* lo, DWORD* hi, DWORD* vt) {  // owner = agent+4 = part-4; id at owner+8
    *lo = *hi = *vt = 0;
    __try {
        const u8* o = *(const u8* const*)(part - 4);
        if (o) *vt = *(const DWORD*)o, *lo = *(const DWORD*)(o + 8), *hi = *(const DWORD*)(o + 0xC);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
static int SlIndexIn(const u8* master, const u8* slave, DWORD partVt) {  // slave's index in master's live list, or -1
    __try {
        if (*(const DWORD*)master != partVt) return -1;  // master freed (its memory reused): not the list we recorded
        const LONG n = *(const LONG*)(master + 0xC);
        u8* const* b = *(u8* const* const*)(master + 0x10);
        u8* const* e = *(u8* const* const*)(master + 0x14);
        for (LONG i = 0; i < n && b + i < e; i++)
            if (b[i] == slave) return (int)i;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return -1;
}
static void SlLock() { while (InterlockedCompareExchange(&g_slBusy, 1, 0)) YieldProcessor(); }
static void SlUnlock() { InterlockedExchange(&g_slBusy, 0); }
static void SlForget(const u8* master) {
    for (int i = 0; i < SL_CAP; i++)
        if (g_slPairs[i].master == master) g_slPairs[i].master = NULL;
}

static void __fastcall SlSetHook(u8* part, void*, u8** arr, DWORD n, DWORD flag) {
    g_slSet(part, NULL, arr, n, flag);
    SlLock();
    SlForget(part);
    if (arr && (LONG)n > 0) {
        ts3native_slavelink.sets++;
        DWORD mLo, mHi, mVt;
        SlOwner(part, &mLo, &mHi, &mVt);
        const DWORD partVt = *(const DWORD*)part;
        for (DWORD i = 0; i < n; i++) {
            if (!arr[i]) continue;
            int f = -1;
            for (int j = 0; j < SL_CAP && f < 0; j++)
                if (!g_slPairs[j].master) f = j;
            for (int j = 0; j < SL_CAP && f < 0; j++)  // full: reuse a pair its master no longer lists
                if (SlIndexIn(g_slPairs[j].master, g_slPairs[j].slave, g_slPairs[j].mPartVt) < 0) f = j;
            if (f < 0) { ts3native_slavelink.overflow++; continue; }
            SlPair& p = g_slPairs[f];
            p.master = part, p.slave = arr[i], p.mPartVt = partVt, p.mLo = mLo, p.mHi = mHi, p.mVt = mVt;
            SlOwner(arr[i], &p.sLo, &p.sHi, &p.sVt);
        }
    }
    SlUnlock();
}

static void __fastcall SlDtorHook(u8* part, void*) {
    SlLock();
    SlForget(part);  // a master going away: its slaves hold no pointer back
    for (int i = 0; i < SL_CAP; i++) {
        SlPair p = g_slPairs[i];
        if (!p.master || p.slave != part) continue;
        g_slPairs[i].master = NULL;
        const int at = SlIndexIn(p.master, part, p.mPartVt);
        if (at < 0) continue;  // EA cleared or replaced that list already
        // Take this one entry out (SetSlaves keeps end == begin + count); the last one goes the game's own way, SetSlaves(0,0,0).
        LONG n = 0;
        __try {
            n = *(LONG*)(p.master + 0xC);
            u8** b = *(u8***)(p.master + 0x10);
            for (LONG j = at; j + 1 < n; j++) b[j] = b[j + 1];
            if (n > 1) *(LONG*)(p.master + 0xC) = n - 1, *(u8***)(p.master + 0x14) = b + (n - 1);
        } __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
        if (n <= 1) g_slSet(p.master, NULL, NULL, 0, 0);
        const LONG k = InterlockedIncrement(&ts3native_slavelink.unlinks);
        if (k <= 16 || (k & (k - 1)) == 0) {
            char st[600];
            if (FbStackText(st, sizeof(st), 12) <= 0) strcpy_s(st, "none");
            Log("ea fix: SlaveUnlink #%ld: entity %p (owner %08lX%08lX vt %08lX) destroyed while slave %d of %ld of master %p "
                "(owner %08lX%08lX vt %08lX): taken off the master's slave list; script stack: %s",
                k, part, p.sHi, p.sLo, p.sVt, at, n, p.master, p.mHi, p.mLo, p.mVt, st);
        }
    }
    SlUnlock();
    g_slDtor(part, NULL);
}

static bool SlDead(const u8* s) {  // destructor ran: base vtable, pure slot +0x18; unreadable counts too (the call would fault)
    __try { return (*(const DWORD* const*)s)[6] == SL_PURECALL; } __except (EXCEPTION_EXECUTE_HANDLER) { return true; }
}
static u8* __fastcall SlTripGet(u8* part, void*, int i) {
    u8* s = g_slGet(part, NULL, i);
    if (!s || !SlDead(s)) return s;
    const LONG k = InterlockedIncrement(&ts3native_slavelink.tripHits);
    if (k <= 16 || (k & (k - 1)) == 0) {
        DWORD mLo, mHi, mVt, sVt = 0;
        SlOwner(part, &mLo, &mHi, &mVt);
        __try { sVt = *(const DWORD*)s; } __except (EXCEPTION_EXECUTE_HANDLER) {}
        Log("ea fix: DeadSlaveTrip #%ld: master %p (owner %08lX%08lX vt %08lX) slave %d %p has a destroyed entity's vtable %08lX: "
            "answered null (the game would call its pure slot)", k, part, mHi, mLo, mVt, i, s, sVt);
    }
    return NULL;
}

static bool ApplySlaveLink(const wchar_t* ini) {
    bool keep = false;
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP ea fix SlaveUnlink / DeadSlaveTrip: no EA 1.69 address, docs/232 §8"); return false; }
#endif
    if (GetPrivateProfileIntW(L"EaCrashFixes", L"SlaveUnlink", 1, ini)) {
        if (FpCallOk(SL_SET_SITE, SL_SET) && FpCallOk(SL_DTOR_SITE, SL_DTOR) && FpRedirect(SL_SET_SITE, (const void*)&SlSetHook) &&
            FpRedirect(SL_DTOR_SITE, (const void*)&SlDtorHook)) {
            ts3native_slavelink.unlinkOn = 1, keep = true;
            Log("OK   ea fix: SlaveUnlink (docs/143 §8.11): a destroyed slave (a boat's driver, a stroller, a led horse) is taken "
                "off its master's slave list before it is freed");
        } else Log("SKIP ea fix: SlaveUnlink: calls at 0x%08X / 0x%08X differ: not installed", SL_SET_SITE, SL_DTOR_SITE);
    }
    if (GetPrivateProfileIntW(L"EaCrashFixes", L"DeadSlaveTrip", 1, ini)) {
        bool ok = true;
        for (int i = 0; i < 4; i++) ok = ok && FpCallOk(kSlTripSites[i], SL_GET);
        for (int i = 0; i < 4 && ok; i++) ok = FpRedirect(kSlTripSites[i], (const void*)&SlTripGet);
        if (ok) {
            ts3native_slavelink.tripOn = 1, keep = true;
            Log("OK   ea fix: DeadSlaveTrip (TEST TRIPWIRE, docs/143 §8.11): avoidance skips and logs a destroyed slave 0");
        } else Log("SKIP ea fix: DeadSlaveTrip: a call to 0x%08X differs: not installed", SL_GET);
    }
    return keep;
}

// [EaCrashFixes] VbFreeDelayMs (docs/67 §44, default 0 = off, Steam 1.67 only): EA use after free of a vertex buffer wrapper.
// 0xA85DF0 releases the wrapper at +0x70 of its owner (0xEC17A0, last ref: deleting dtor 0x61A410 / 0x61A340, then free
// 0x4E3F70) while a queued draw item still runs the draw callback 0xA84D00 with the old pointer (VbGuard caught it 23-894 ms
// after the release, same thread). Freed memory reused = Device_SetStreamSource 0x60F020 hands garbage to the device (the
// bridge crashes at d3d9.dll+0x3A91D / +0x2789B). Fix: the two deleting dtors run without their free; the wrapper (base
// vtable, D3D VB +0x14 = 0, the game's own released state) is freed VbFreeDelayMs later, so a late draw binds a NULL stream,
// as the not yet reused memory already did in the caught cases. Its refcount is pinned high so a late Release cannot free it twice.
#ifndef TS3A_TABLE
static const DWORD kVqDelA = 0x0061A340, kVqDelB = 0x0061A410, kVqFree = 0x004E3F70;
static const u8 kVqPro[] = {0x56, 0x8B, 0xF1, 0x57, 0x8D, 0x7E, 0x08};  // push esi ; mov esi,ecx ; push edi ; lea edi,[esi+8]
typedef void*(__fastcall* VqDelFn)(u8* self, void* edx, DWORD flags);
static VqDelFn g_vqOrig[2];
static const DWORD kVqCap = 8192;
static u8* g_vqPtr[kVqCap];
static DWORD g_vqTick[kVqCap], g_vqHead, g_vqN, g_vqMs;
static SRWLOCK g_vqLock = SRWLOCK_INIT;
static volatile LONG g_vqForced;
static void VqPush(u8* self) {
    *(volatile LONG*)(self + 4) = 0x40000000;  // refcount (0xEC17A0 left 1): a late Release never reaches 0
    u8* due[16];
    int n = 0;
    const DWORD now = GetTickCount();
    AcquireSRWLockExclusive(&g_vqLock);
    while (g_vqN && n < 16 && (now - g_vqTick[g_vqHead] >= g_vqMs || g_vqN == kVqCap)) {
        if (g_vqN == kVqCap && InterlockedIncrement(&g_vqForced) == 1) Log("ea fix: VbFreeDelay: queue full (%lu), freeing early", kVqCap);
        due[n++] = g_vqPtr[g_vqHead], g_vqHead = (g_vqHead + 1) % kVqCap, g_vqN--;
    }
    const DWORD t = (g_vqHead + g_vqN) % kVqCap;
    g_vqPtr[t] = self, g_vqTick[t] = now, g_vqN++;
    ReleaseSRWLockExclusive(&g_vqLock);
    for (int i = 0; i < n; i++) ((void(__cdecl*)(void*))kVqFree)(due[i]);
}
static void* __fastcall VqDelA(u8* self, void* edx, DWORD flags) {
    void* r = g_vqOrig[0](self, edx, flags & ~1u);
    if (flags & 1) VqPush(self);
    return r;
}
static void* __fastcall VqDelB(u8* self, void* edx, DWORD flags) {
    void* r = g_vqOrig[1](self, edx, flags & ~1u);
    if (flags & 1) VqPush(self);
    return r;
}
static bool ApplyVbFreeDelay(const wchar_t* ini) {
    g_vqMs = GetPrivateProfileIntW(L"EaCrashFixes", L"VbFreeDelayMs", 0, ini);
    if (!g_vqMs) return false;
    if (g_vqMs > 10000) g_vqMs = 10000;
    g_vqOrig[0] = (VqDelFn)InstallDetour("ea fix VbFreeDelay VB wrapper dtor 0x61A340", kVqDelA, kVqPro, sizeof(kVqPro), (void*)VqDelA, &g_vqOrig[0]);
    g_vqOrig[1] = (VqDelFn)InstallDetour("ea fix VbFreeDelay VB wrapper dtor 0x61A410", kVqDelB, kVqPro, sizeof(kVqPro), (void*)VqDelB, &g_vqOrig[1]);
    const bool ok = g_vqOrig[0] && g_vqOrig[1];
    Log("%s ea fix: VbFreeDelay (docs/67 §44): VB wrappers freed %lu ms after their destructor (late draw 0xA84D00 binds NULL, "
        "not reused memory)", ok ? "OK  " : "PARTIAL", g_vqMs);
    return ok;
}
#else
static bool ApplyVbFreeDelay(const wchar_t* ini) {  // EA 1.69: the VB wrapper dtor sites are unmapped
    if (GetPrivateProfileIntW(L"EaCrashFixes", L"VbFreeDelayMs", 0, ini))
        Log("SKIP ea fix: VbFreeDelay (docs/67 §44): Steam 1.67 only, no EA 1.69 rows: not installed");
    return false;
}
#endif

static bool ApplyEaCrashFixes(const wchar_t* ini) {
    bool keep = ApplyConformActor(ini);
    ApplyThumbPose(ini);
    keep = ApplyThumbPoseBones(ini) || keep;
    keep = ApplyGeomBoneIndex(ini) || keep;
    keep = ApplyLotLevelPastEnd(ini) || keep;
    keep = ApplyGeomNoMaterial(ini) || keep;
    keep = ApplyMintDivGuard(ini) || keep;
    keep = ApplySlaveLink(ini) || keep;
    keep = ApplyVbFreeDelay(ini) || keep;
#ifdef TS3N_EA_PORT
    keep = ApplySimNoTexture(ini) || keep;
    keep = ApplySimNoTextureAtlas(ini) || keep;
#endif
    return keep;
}
