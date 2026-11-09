// [MonoSigGuard] (docs/539 §20, 0.36.232): EA bug fix, release ini Enabled=1 (rare-crash rule: seen twice, cause in the disassembly).
// 0xD70F90 (called only from ScriptHost_InvokeDelegate 0xD75FA0 at 0xD75FD3; eax = the delegate handle, answer in al, plain ret,
// keeps esi / edi) asks "is the delegate's Invoke one Object[] parameter?": mono_get_delegate_invoke 0xEA13C0, mono_method_signature
// 0xE85040, then `mov edi,[esi+0x10]` (params[0]) and mono_type_get_type 0xF4AC60 (`movzx eax,byte [arg+6]`) BEFORE
// `cmp word [esi+4],1` (param_count) [verified disassembly TS3W 1.67]. A zero-parameter Invoke (GameUtils.SwapLoadScreen ->
// 0x7F1760 -> global 0x11D8C4C -> 0x7F1480, every loading screen older than 2 s) has a 0x10-byte signature, so params[0] is the
// next allocation's first dword: two travel-screen crashes reading [1+6] at 0xF4AC64 (float5-round3 all-off-1 / -2, 2026-11-09).
// Fix: in place of the 9 bytes at 0xD70FB2 (mov edi,[esi+0x10]; push edi; call 0xF4AC60) the stub checks param_count first. Not 1:
// jump to EA's own "no" tail 0xD70FEB (pop edi; xor al,al; pop esi; ret), which vanilla reaches for every param_count != 1 anyway,
// so only the out-of-bounds read goes. 1: the same three instructions, back at 0xD70FBB (add esp,4).
// Log=1: "mono sig guard:" lines for param_count 0 only (0.36.233), first 20 a session then every 1000th; counters v184 msg* every time.
static const DWORD MSG_SITE = 0x00D70FB2, MSG_CONT = 0x00D70FBB, MSG_FAIL = 0x00D70FEB, MSG_GETTYPE = 0x00F4AC60;
static const DWORD MSG_RET = 0x00D75FD8;  // return address into ScriptHost_InvokeDelegate 0xD75FA0 (its only call)
// mov edi,[esi+10h]; push edi; call 0xF4AC60 (written over) | add esp,4; cmp word [esi+4],1; jne 0xD70FEB (checked, kept)
static const u8 kMsgSite[19] = {0x8B, 0x7E, 0x10, 0x57, 0xE8, 0xA5, 0x9C, 0x1D, 0x00, 0x83, 0xC4, 0x04, 0x66, 0x83, 0x7E, 0x04, 0x01, 0x75, 0x26};
static DWORD g_msgCont = MSG_CONT, g_msgFail = MSG_FAIL, g_msgGetType = MSG_GETTYPE;  // stub targets (selftest swaps them)
static bool g_msgLog;
static volatile LONG g_msgZero;  // param_count 0 hits (the logged ones)

// sig = the Invoke MonoMethodSignature, ret = the stub's caller's return address, ebp = 0xD75FA0's frame (ebp+0x6C = its return).
static void __stdcall MsgHit(const u8* sig, DWORD ret, DWORD ebp) {
    const WORD pc = *(const volatile WORD*)(sig + 4);
    const LONG n = InterlockedIncrement(&ts3native_counters.msgSkip);
    ts3native_counters.msgParamCount = pc;
    // 0.36.233: lines only for param_count 0 (params[0] past the signature: the SwapLoadScreen crash case). param_count > 1
    // (13/6/4/3 in float5-round4 all-off-1: 124 lines, 103,000 skips) is counted only: vanilla reads an in-bounds params[0]
    // there and then takes the same "no" tail 0xD70FEB (jne after cmp word [esi+4],1), so skipping changes nothing.
    if (pc) return;
    const LONG z = InterlockedIncrement(&g_msgZero);
    if (!g_msgLog || (z > 20 && z % 1000)) return;
    DWORD p0 = 0, caller = ret;
    const char* p0note = " (past a 0x10-byte signature: the next block's data)";
    __try { p0 = *(const volatile DWORD*)(sig + 0x10); } __except (EXCEPTION_EXECUTE_HANDLER) { p0note = " (unreadable)"; }
    if (ret == MSG_RET) __try { caller = *(const volatile DWORD*)(uintptr_t)(ebp + 0x6C); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    Log("mono sig guard: delegate sig param_count %u, params[0] 0x%08lX%s, caller 0x%08lX%s; not read, EA's not-one-Object[] answer (false); "
        "skips %ld (param_count 0: %ld)", pc, p0, p0note, caller, ret == MSG_RET ? " (of ScriptHost_InvokeDelegate 0xD75FA0)" : " (0xD70F90 return)", n, z);
}

// In place of the 9 bytes at 0xD70FB2: esi = the signature (non-NULL), [esp] = saved edi, [esp+4] = saved esi, [esp+8] = return.
static __declspec(naked) void MsgStub() {
    __asm {
        cmp word ptr [esi + 4], 1
        jne bad
        mov edi, [esi + 0x10]
        push edi
        call dword ptr [g_msgGetType]
        jmp dword ptr [g_msgCont]     // add esp,4 and EA's own param_count / type checks
    bad:
        pushad
        push ebp
        push dword ptr [esp + 0x2C]   // pushad 0x20 + ebp 4: the return address
        push esi
        call MsgHit
        popad
        jmp dword ptr [g_msgFail]     // pop edi; xor al,al; pop esi; ret
    }
}

static bool MsgPatch(u8* site) {
    if (!Matches(site, kMsgSite, sizeof(kMsgSite))) return false;
    u8 b[9] = {0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90, 0x90};
    *(LONG*)(b + 1) = (LONG)((uintptr_t)&MsgStub - ((uintptr_t)site + 5));
    return Write(site, b, sizeof(b));  // DllMain: Mono is not started yet
}

static bool ApplyMonoSigGuard(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"MonoSigGuard", L"Enabled", 1, ini) <= 0) return false;
    g_msgLog = GetPrivateProfileIntW(L"MonoSigGuard", L"Log", 1, ini) > 0;
    if (!MsgPatch((u8*)(uintptr_t)MSG_SITE)) {
        Log("SKIP mono sig guard: bytes at 0x%08lX differ: not installed", MSG_SITE);
        return false;
    }
    ts3native_counters.msgOn = 1;
    Log("OK   mono sig guard (docs/539 §20): 0xD70F90 checks the delegate's param_count before reading params[0] (zero-parameter "
        "SwapLoadScreen crash 0xF4AC64)%s", g_msgLog ? "; \"mono sig guard:\" lines for param_count 0 (20, then every 1000th)" : "; counters only (Log=0)");
    return g_msgLog;
}
