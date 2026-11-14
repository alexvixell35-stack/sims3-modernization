// TestMonoSigGuard ([MonoSigGuard] monosig.cpp, docs/539 §20). Included by selftest.cpp.
// MsgStub runs with esi = a fake signature and the 0xD70F90 frame on the stack; the two targets are landing pads.
static DWORD g_mstSp, g_mstKind, g_mstEax, g_mstEsi, g_mstEdi, g_mstEbp, g_mstTop;
static __declspec(naked) void MstBack() {  // ecx = 1 EA's "no" tail, 2 the next instruction (eax = the type it read)
    __asm {
        mov g_mstKind, ecx
        mov g_mstEax, eax
        mov g_mstEsi, esi
        mov g_mstEdi, edi
        mov g_mstEbp, ebp
        mov eax, [esp]
        mov g_mstTop, eax             // the cont path still has the pushed argument on top; the fail path the saved edi
        mov esp, g_mstSp
        pop ebp
        pop edi
        pop esi
        pop ebx
        ret
    }
}
static __declspec(naked) void MstFail() { __asm { mov ecx, 1 } __asm { jmp MstBack } }
static __declspec(naked) void MstCont() { __asm { mov ecx, 2 } __asm { jmp MstBack } }
static int __cdecl MstGetType(const u8* t) { return t[6]; }  // mono_type_get_type 0xF4AC60
static __declspec(naked) void __cdecl MstRun(const void* /*sig*/, DWORD /*ret*/, DWORD /*ebp*/) {
    __asm {
        push ebx
        push esi
        push edi
        push ebp
        mov g_mstSp, esp
        mov esi, [esp + 20]
        mov ebp, [esp + 28]
        push dword ptr [esp + 24]     // return address into 0xD75FA0
        push 0x11111111               // saved esi
        push 0x22222222               // saved edi
        mov edi, 0x5A5A5A5A
        jmp MsgStub
    }
}

static void TestMonoSigGuard() {
    static_assert(offsetof(Ts3nCounters, msgOn) == 0x5F70 && offsetof(Ts3nCounters, msgSpare) == 0x5F7C && sizeof(Ts3nCounters) == 6337 * 4,
                  "counters v184 layout after v183 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    const DWORD t0[3] = {g_msgCont, g_msgFail, g_msgGetType};
    const bool log0 = g_msgLog;
    g_msgCont = (DWORD)(uintptr_t)&MstCont, g_msgFail = (DWORD)(uintptr_t)&MstFail, g_msgGetType = (DWORD)(uintptr_t)&MstGetType;
    g_msgLog = true, k.msgSkip = 0;
    const LONG z0 = g_msgZero;
    alignas(4) static u8 type[8] = {0, 0, 0, 0, 0, 0, 0x1D, 0}, sig[0x18];
    *(WORD*)(sig + 4) = 1, *(DWORD*)(sig + 0x10) = (DWORD)(uintptr_t)type;
    // one parameter: EA's read, back at 0xD70FBB with the type in eax and params[0] in edi
    MstRun(sig, 0x12345678, 0x6B6B6B6B);
    assert(g_mstKind == 2 && g_mstEax == 0x1D && g_mstEdi == (DWORD)(uintptr_t)type && g_mstTop == (DWORD)(uintptr_t)type &&
           g_mstEsi == (DWORD)(uintptr_t)sig && g_mstEbp == 0x6B6B6B6B && k.msgSkip == 0);
    // zero parameters, params[0] = 1 (the crash): not read, EA's "no" tail with edi / esi / ebp kept and the frame intact
    *(WORD*)(sig + 4) = 0, *(DWORD*)(sig + 0x10) = 1;
    static DWORD frame[0x20];
    frame[0x6C / 4] = 0x007F14B1;  // 0xD75FA0's return address (SwapLoadScreen's 0x7F1480)
    MstRun(sig, MSG_RET, (DWORD)(uintptr_t)frame);
    assert(g_mstKind == 1 && g_mstEdi == 0x5A5A5A5A && g_mstTop == 0x22222222 && g_mstEsi == (DWORD)(uintptr_t)sig &&
           g_mstEbp == (DWORD)(uintptr_t)frame && k.msgSkip == 1 && k.msgParamCount == 0 && g_msgZero == z0 + 1);  // logged
    // two parameters: also EA's "no" (vanilla answers false for any param_count != 1)
    *(WORD*)(sig + 4) = 2, *(DWORD*)(sig + 0x10) = (DWORD)(uintptr_t)type;
    MstRun(sig, 0x12345678, 0x6B6B6B6B);
    assert(g_mstKind == 1 && k.msgSkip == 2 && k.msgParamCount == 2 && g_msgZero == z0 + 1);  // counted, no line (0.36.233)
    // patch on a stand-in site; foreign bytes write nothing, a second patch finds our jmp
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    memcpy(code, kMsgSite, sizeof(kMsgSite));
    code[18] = 0x27;
    assert(!MsgPatch(code) && !memcmp(code, kMsgSite, 18));
    code[18] = 0x26;
    assert(MsgPatch(code) && code[0] == 0xE9 && (uintptr_t)code + 5 + *(LONG*)(code + 1) == (uintptr_t)&MsgStub && code[8] == 0x90 &&
           !memcmp(code + 9, kMsgSite + 9, 10));
    assert(!MsgPatch(code));
    VirtualFree(code, 0, MEM_RELEASE);
    g_msgCont = t0[0], g_msgFail = t0[1], g_msgGetType = t0[2], g_msgLog = log0, k.msgSkip = k.msgParamCount = 0;
    printf("monosig: one parameter reads EA's way, zero / two parameters skip params[0] to EA's no tail with edi kept, lines for zero only, patch bytes: ok\n");
}
