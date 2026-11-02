// [MonoFast] CallPath: own mint handlers for call (0xEA), callvirt (0xEC), ret (0x06) and ret.void (0x07) (docs/139 §5,
// docs/139 §8 as built). Included by ts3native.cpp (before autshadow.cpp: Satb chains over our callvirt entry) and by
// native/monohost/monohost.cpp (inside namespace mt, MONOHOST_MINTCALL=fast|off|verify). The includer defines MC_CTR, a
// struct with the mc* LONG fields below (ts3native: ts3native_counters v143).
//
// EA's path for one interpreted call is ~190 instructions [verified, capstone; docs/139 §5]: the call / callvirt handler
// (0xE5493E / 0xE549EB) spills ip, sp, method, this and the argument pointer to its C frame, jumps to the common push
// 0xE56287, which saves the caller into the frame record, bumps the depth, takes the slot's MonoInvocation, tests the
// transform flag, bumps the arena, tests for an EA native body and sets up the callee's areas; ret (0xE591FF / 0xE59236)
// copies the value and runs the frame return 0xE543C7 (depth, restore, arena pop, exception / newobj / yield tests).
//
// Each handler here tests every condition under which EA's path would do anything else (alloca of a new MonoInvocation,
// transform, native body, arena chunk change, depth limit, remoting check, value-type receiver, cache miss, exception,
// newobj return, yield, the "stack not empty" log, depth 0) BEFORE it writes anything, and on any of them jumps to EA's
// handler with the entry state unchanged (eax = edx = opcode, edi = ip, esi = sp; frame scratch slots it wrote are ones
// EA's handler writes first on every path). Otherwise it writes exactly the words EA writes, in one pass, and dispatches
// the next instruction with its own copy of the dispatch head.
//
// Switch: MC_CTR.mcSwitch (ini [MonoFast] CallPath, harness --ab mintcall): 0 = every call bails to EA's handler (one
// extra indirect jump), 1 = fast path. Verify mode (CallPath=2): every jump-table entry goes through McVCatch; each fast
// call snapshots what both paths may write, runs the fast writes, records them, restores the snapshot and runs EA's
// handler from the same state; at the next dispatch the bytes and edi / esi / esp must be equal (mcMismatch).

#ifndef MC_CTR
#error define MC_CTR (the struct holding the mc* counters) before including mintcall.cpp
#endif

static const DWORD MC_TABLE = 0x00E59880, MC_HEAD = 0x00E544D5, MC_NOP = 0x1C1;
static const DWORD MC_OPS[4] = {0x06, 0x07, 0xEA, 0xEC};
static const DWORD MC_EA[4] = {0x00E591FF, 0x00E59236, 0x00E5493E, 0x00E549EB};
// EA code our handlers model (FNV-1a over the Steam 1.67 bytes): any other hook in them (ArenaTail TailWatch 0xE563B2,
// monohost MINT=count 0xE54B0B, ...) changes what the stock path does, so the fast path refuses to install.
// MonoPatcher 0.3.0 (docs/68 §1.3) is accepted: its DllMain patch 0xE59218 `jbe` -> `90 E9` only drops the "ret: more
// values on stack" print (our ret bails on that case and EA's then returns either way), and its on-demand ForceJIT
// (0xE5637D `jne` -> `90 90`: transform on every call) is tested per call (byte 0x75 expected): calls go to EA then.
static const struct { DWORD at, n, fnv, fnv2; } kMcCode[5] = {
    {0x00E5493E, 0xAD, 0x51DF97BD, 0},           // call / con.call
    {0x00E549EB, 0x160, 0x65BD44CB, 0},          // callvirt family
    {0x00E56287, 0x1C2, 0x3D55ED25, 0},          // common push
    {0x00E591FF, 0x60, 0x41B1FFB3, 0x6B9C5C8D},  // ret / ret.void / ret.vt (2nd: MonoPatcher's 0xE59218 patch)
    {0x00E543C7, 0x127, 0x86DC9168, 0},          // frame return + dispatch head
};
static DWORD g_mcEaRet = 0x00E591FF, g_mcEaRetVoid = 0x00E59236, g_mcEaCall = 0x00E5493E, g_mcEaCallvirt = 0x00E549EB;
static DWORD g_mcHead = MC_HEAD;
static int g_mcVerify;          // verify mode installed (all entries -> McVCatch)
static void* g_mcOrig[MC_NOP];  // verify: what each entry pointed at (our four handlers for our ops)
static void McPush();
static void McRetTail();
static void McVPost();
static void __cdecl McVPre(int kind, DWORD* regs);

// ---- handlers (entry state of every mint handler: eax = edx = opcode, edi = ip, esi = sp, ebp = ves_exec frame;
// ebx / ecx / flags not live) ----

// common tail of call / callvirt once every check passed: ebx = callee RuntimeMethod, edx = &rec[depth+1], eax = arena
// chunk (room checked), edi = caller ip after the call instruction, esi = caller sp after popping args (and this);
// [ebp-0x48] args, [ebp-0x50] this, [ebp-0x54] return slot already written. Mirrors 0xE56287-0xE5642E + 0xE54B44.
static __declspec(naked) void McPush() {
    __asm {
        mov ecx, [ebp - 8]
        mov [edx - 0x130], ecx          // rec[d]: caller MonoInvocation, ip, sp, the two area pointers
        mov [edx - 0x12C], edi
        mov [edx - 0x128], esi
        mov ecx, [ebp - 0xC]
        mov [edx - 0x124], ecx
        mov ecx, [ebp - 0x14]
        mov [edx - 0x120], ecx
        mov ecx, [ebp + 8]              // state: depth++, max depth
        mov esi, [ecx + 0x140]
        inc esi
        mov [ecx + 0x140], esi
        cmp esi, [ecx + 0x144]
        jle p_max
        mov [ecx + 0x144], esi
    p_max:
        mov [ebp - 0x2C], edx
        xor ecx, ecx
        mov [edx + 0x128], ecx          // finally stack depth, pending clause
        mov [edx + 0x12C], ecx
        mov [edx + 0x14], ecx           // newobj flag ([ebp-0x28] = 0)
        mov esi, [edx]                  // the slot's MonoInvocation
        mov edi, [ebp - 8]
        mov [esi], edi                  // parent
        mov [esi + 4], ebx
        mov edi, [ebp - 0x48]
        mov [esi + 0x14], edi
        mov edi, [ebp - 0x54]
        mov [esi + 8], edi
        mov edi, [ebp - 0x50]
        mov [esi + 0xC], edi
        mov [esi + 0x20], ecx
        mov [esi + 0x1C], ecx
        mov [esi + 0x24], ecx
        mov edi, [ebp + 0xC]            // ThreadContext +8 = current frame
        mov [edi + 8], esi
        mov [ebp - 8], esi
        mov [ebp - 0x10], ebx
        mov edi, [eax]                  // arena bump (inline path 0xE563B2)
        mov [esi + 0x10], edi
        movzx ecx, word ptr [ebx + 0x38]
        add [eax], ecx
        movzx ecx, word ptr [ebx + 0x3E]
        add edi, ecx                    // eval stack base
        mov [edx + 8], edi
        mov [esi + 0x18], edi
        mov esi, edi
        movzx ecx, word ptr [ebx + 0x34]
        add ecx, edi
        mov [edx + 0xC], ecx
        mov [ebp - 0xC], ecx
        movzx eax, word ptr [ebx + 0x36]
        add ecx, eax
        mov [edx + 0x10], ecx
        mov [ebp - 0x14], ecx
        mov edi, [ebx]                  // callee code
        mov [edx + 4], edi
        mov [ebp - 4], edi
        test dword ptr [ebx + 0x44], 0x800
        jnz p_init
    p_next:
        cmp g_mcVerify, 0
        jne p_post
        movzx eax, word ptr [edi]       // dispatch head copy (0xE544D5)
        movzx edx, ax
        cmp edx, 0x1C0
        ja p_head
        jmp dword ptr [edx * 4 + 0x00E59880]
    p_head:
        jmp dword ptr [g_mcHead]
    p_post:
        jmp McVPost
    p_init:                             // init locals: memset([ebp-0x14], 0, rm+0x3C) as EA's CRT call
        movzx edx, word ptr [ebx + 0x3C]
        add edx, ecx
        xor eax, eax
    p_z4:
        lea ebx, [ecx + 4]
        cmp ebx, edx
        ja p_z1
        mov [ecx], eax
        mov ecx, ebx
        jmp p_z4
    p_z1:
        cmp ecx, edx
        jae p_next
        mov byte ptr [ecx], al
        inc ecx
        jmp p_z1
    }
}

static __declspec(naked) void McCall() {  // 0xEA
    __asm {
        cmp MC_CTR.mcSwitch, 0
        je c_slow
        mov ecx, [ebp - 0x10]
        mov ecx, [ecx + 0x14]           // data items
        movzx eax, word ptr [edi + 2]
        mov ebx, [ecx + eax * 4]        // callee RuntimeMethod
        movzx ecx, byte ptr [ebx + 0x40]
        neg ecx
        lea ecx, [esi + ecx * 8]        // first argument
        xor edx, edx
        test dword ptr [ebx + 0x44], 0x200
        jz c_this
        mov edx, [ecx - 8]              // this
        test dword ptr [ebx + 0x44], 0x400
        jnz c_this
        test edx, edx                   // EA's remoting check reads this->vtable->klass (faults on null: EA's path)
        jz c_slow
        mov eax, [edx]
        test eax, eax
        jz c_slow
        mov eax, [eax]
        cmp eax, dword ptr ds:[0x01220EC0]
        je c_slow
    c_this:
        mov [ebp - 0x48], ecx           // scratch slots EA's handler writes first on every path
        mov [ebp - 0x50], edx
#ifndef MC_NOFJ
        cmp byte ptr ds:[0x00E5637D], 0x75  // MonoPatcher ForceJIT active: EA transforms on every call
        jne c_slow
#endif
        test dword ptr [ebx + 0x44], 0x100  // transformed
        jz c_slow
        cmp dword ptr [ebx + 0x24], 0   // EA native body
        jne c_slow
        mov eax, [ebp + 8]
        mov ecx, [eax + 0x140]
        inc ecx
        cmp ecx, [ebp - 0x30]           // slot below the high-water mark: its MonoInvocation exists, no alloca
        jge c_slow
        imul ecx, ecx, 0x130
        lea edx, [ebp + ecx - 0x4C74]
        cmp dword ptr [edx], 0
        je c_slow
        mov eax, [eax + 0x13C]          // arena chunk with room
        test eax, eax
        jz c_slow
        movzx ecx, word ptr [ebx + 0x38]
        add ecx, [eax]
        cmp ecx, [eax + 4]
        ja c_slow
        cmp g_mcVerify, 0
        jne c_pre
    c_go:
        mov ecx, [ebp - 8]
        mov [ecx + 0x1C], edi           // throw ip
        mov ecx, [ebp - 0xC]
        mov [esi], ecx
        mov [ebp - 0x54], esi           // return slot
        add edi, 4
        mov esi, [ebp - 0x48]
        test dword ptr [ebx + 0x44], 0x200
        jz c_push
        sub esi, 8
    c_push:
        jmp McPush
    c_pre:
        pushad
        push esp
        push 2
        call McVPre
        add esp, 8
        popad
        jmp c_go
    c_slow:
        inc MC_CTR.mcBailCall
        mov eax, 0xEA
        mov edx, eax
        jmp dword ptr [g_mcEaCall]
    }
}

static __declspec(naked) void McCallvirt() {  // 0xEC (con.callvirt / con.boxcallvirt keep EA's handler)
    __asm {
        cmp MC_CTR.mcSwitch, 0
        je v_slow
        mov ecx, [ebp - 0x10]
        mov ecx, [ecx + 0x14]
        movzx eax, word ptr [edi + 2]
        lea eax, [ecx + eax * 4]        // inline cache pair (class, RuntimeMethod)
        movzx edx, word ptr [edi + 6]
        mov edx, [ecx + edx * 4]        // declared method (parameter count)
        movzx ecx, byte ptr [edx + 0x40]
        neg ecx
        lea ecx, [esi + ecx * 8]
        mov [ebp - 0x48], ecx
        mov edx, [ecx - 8]              // receiver
        mov [ebp - 0x50], edx
        test edx, edx
        jz v_slow
        mov edx, [edx]
        test edx, edx
        jz v_slow
        mov edx, [edx]                  // vtable->klass
        cmp edx, [eax]
        jne v_slow                      // cache miss: EA's lookup and cache write
        test byte ptr [edx + 0x14], 8   // value-type receiver (unbox adjust)
        jnz v_slow
        mov ebx, [eax + 4]
#ifndef MC_NOFJ
        cmp byte ptr ds:[0x00E5637D], 0x75
        jne v_slow
#endif
        test dword ptr [ebx + 0x44], 0x100
        jz v_slow
        cmp dword ptr [ebx + 0x24], 0
        jne v_slow
        mov eax, [ebp + 8]
        mov ecx, [eax + 0x140]
        inc ecx
        cmp ecx, [ebp - 0x30]
        jge v_slow
        imul ecx, ecx, 0x130
        lea edx, [ebp + ecx - 0x4C74]
        cmp dword ptr [edx], 0
        je v_slow
        mov eax, [eax + 0x13C]
        test eax, eax
        jz v_slow
        movzx ecx, word ptr [ebx + 0x38]
        add ecx, [eax]
        cmp ecx, [eax + 4]
        ja v_slow
        cmp g_mcVerify, 0
        jne v_pre
    v_go:
        mov ecx, [ebp - 8]
        mov [ecx + 0x1C], edi
        mov ecx, [ebp - 0xC]
        mov [esi], ecx
        mov [ebp - 0x54], esi
        add edi, 8
        mov esi, [ebp - 0x48]
        sub esi, 8
        jmp McPush
    v_pre:
        pushad
        push esp
        push 3
        call McVPre
        add esp, 8
        popad
        jmp v_go
    v_slow:
        inc MC_CTR.mcBailCallvirt
        mov eax, 0xEC
        mov edx, eax
        jmp dword ptr [g_mcEaCallvirt]
    }
}

// shared ret checks: ecx = callee MonoInvocation (the eval-stack test is per opcode). Falls through when the inline
// frame return applies; eax = arena chunk then.
#define MC_RET_CHECKS(slow)                                                                                              \
    __asm mov eax, [ebp + 8] __asm cmp dword ptr [eax + 0x140], 0 __asm je slow /* depth 0: exit A */                    \
    __asm cmp byte ptr [eax + 0x149], 0 __asm jne slow /* yield: frame save */                                           \
    __asm cmp dword ptr [ecx + 0x20], 0 __asm jne slow /* pending exception */                                           \
    __asm mov edx, [ebp - 0x2C] __asm cmp dword ptr [edx + 0x14], 0 __asm jne slow /* newobj return */                   \
    __asm mov edx, [ecx + 0x10] __asm test edx, edx __asm jz slow                                                        \
    __asm mov eax, [eax + 0x13C] __asm test eax, eax __asm jz slow                                                       \
    __asm lea ebx, [eax + 0xC] __asm cmp edx, ebx __asm jbe slow /* base inside the current chunk: inline pop */         \
    __asm cmp edx, [eax] __asm ja slow

static __declspec(naked) void McRet() {  // 0x06: 8-byte value -> the return slot
    __asm {
        cmp MC_CTR.mcSwitch, 0
        je r_slow
        mov ecx, [ebp - 8]
        lea ebx, [esi - 8]
        cmp ebx, [ecx + 0x18]           // values left on the eval stack: EA logs
        ja r_slow
    }
    MC_RET_CHECKS(r_slow)
    __asm {
        cmp g_mcVerify, 0
        jne r_pre
    r_go:
        mov ebx, [ecx + 8]
        mov edx, [esi - 8]
        mov [ebx], edx
        mov edx, [esi - 4]
        mov [ebx + 4], edx
        jmp McRetTail
    r_pre:
        pushad
        push esp
        push 0
        call McVPre
        add esp, 8
        popad
        jmp r_go
    r_slow:
        inc MC_CTR.mcBailRet
        mov eax, 0x06
        mov edx, eax
        jmp dword ptr [g_mcEaRet]
    }
}

static __declspec(naked) void McRetVoid() {  // 0x07
    __asm {
        cmp MC_CTR.mcSwitch, 0
        je w_slow
        mov ecx, [ebp - 8]
        cmp esi, [ecx + 0x18]
        ja w_slow
    }
    MC_RET_CHECKS(w_slow)
    __asm {
        cmp g_mcVerify, 0
        jne w_pre
    w_go:
        jmp McRetTail
    w_pre:
        pushad
        push esp
        push 1
        call McVPre
        add esp, 8
        popad
        jmp w_go
    w_slow:
        inc MC_CTR.mcBailRet
        mov eax, 0x07
        mov edx, eax
        jmp dword ptr [g_mcEaRetVoid]
    }
}

// frame return 0xE543C7-0xE544A4 on the inline path: ecx = callee MonoInvocation, eax = arena chunk
static __declspec(naked) void McRetTail() {
    __asm {
        mov edx, [ebp + 8]
        mov ebx, [edx + 0x140]
        dec ebx
        mov [edx + 0x140], ebx
        imul ebx, ebx, 0x130
        lea ebx, [ebp + ebx - 0x4C74]   // caller record
        push dword ptr [ebp - 0x2C]     // callee record (EA keeps it in [ebp-0x34])
        mov edi, [ebx + 4]
        mov esi, [ebx + 8]
        mov edx, [ebx]                  // caller MonoInvocation
        mov [ebp - 8], edx
        mov [ebp - 4], edi
        mov [ebp - 0x2C], ebx
        mov edx, [edx + 4]
        mov [ebp - 0x10], edx
        mov edx, [ebx + 0xC]
        mov [ebp - 0xC], edx
        mov edx, [ebx + 0x10]
        mov [ebp - 0x14], edx
        mov edx, [ebp + 0xC]
        push dword ptr [ebp - 8]
        pop dword ptr [edx + 8]         // ThreadContext +8 = caller frame
        mov edx, [ecx + 0x10]           // arena pop
        mov [eax], edx
        mov dword ptr [ecx + 0x10], 0
        mov edx, [ecx + 4]
        cmp byte ptr [edx + 0x44], 1    // no value to push
        pop edx                         // (flags kept)
        je t_next
        mov edx, [edx]                  // EA copies from the callee record's MonoInvocation return slot
        mov edx, [edx + 8]
        mov eax, [edx]
        mov [esi], eax
        mov eax, [edx + 4]
        mov [esi + 4], eax
        add esi, 8
        mov [ebx + 8], esi
    t_next:
        cmp g_mcVerify, 0
        jne t_post
        movzx eax, word ptr [edi]
        movzx edx, ax
        cmp edx, 0x1C0
        ja t_head
        jmp dword ptr [edx * 4 + 0x00E59880]
    t_head:
        jmp dword ptr [g_mcHead]
    t_post:
        jmp McVPost
    }
}

// ---- verify mode ----
struct McRegs { DWORD edi, esi, ebp, esp_, ebx, edx, ecx, eax; };  // pushad layout
struct McRange { u8* at; DWORD n, off; };
static const int MC_VR = 12;
static const DWORD MC_VBYTES = 24576, MC_VFRAME = 0x80;
struct McV {
    volatile LONG state;  // 0 idle, 3 claimed, 1 fast pass running (snapshot taken), 2 EA pass running (compare at the next
                          // dispatch); one call in flight over all threads, on the thread g_mcvTid
    int kind;
    McRegs r0;  // registers at the pre point (edi / esi = the handler's entry values)
    McRange rg[MC_VR];
    int nr;
    DWORD aEdi, aEsi, aEsp, ea, rm;
    u8 s0[MC_VBYTES], a[MC_VBYTES];
};
static McV* g_mcv;
static DWORD g_mcvTid, g_mcvEvery = 1, g_mcvN;
static DWORD g_mcvJmp;
static bool g_mcvNegative;  // monohost MONOHOST_MCNEG=1: every verified call must show a mismatch
static int g_mcvLogged;

static bool McVAdd(McV& v, DWORD at, DWORD n) {
    DWORD off = v.nr ? v.rg[v.nr - 1].off + v.rg[v.nr - 1].n : 0;
    if (v.nr >= MC_VR || off + n > MC_VBYTES || at < 0x10000) return false;
    v.rg[v.nr++] = {(u8*)(uintptr_t)at, n, off};
    return true;
}

// snapshot what both paths may write (kind = index into MC_OPS / MC_EA: 2 call, 3 callvirt: regs ebx = rm,
// edx = &rec[n], eax = chunk; 0 ret, 1 ret.void: ecx = callee MonoInvocation, eax = chunk)
static void __cdecl McVPre(int kind, DWORD* regs) {
    McRegs* r = (McRegs*)regs;
    McV& v = *g_mcv;
    // fast calls are counted here only (verify mode): an increment per call costs ~1-2 ns in the fast path [measured offline]
    InterlockedIncrement(kind == 2 ? &MC_CTR.mcCall : kind == 3 ? &MC_CTR.mcCallvirt : &MC_CTR.mcRet);
    if (v.state || ++g_mcvN % g_mcvEvery) return;
    if (InterlockedCompareExchange(&v.state, 3, 0) != 0) return;  // another thread is verifying a call
    g_mcvTid = GetCurrentThreadId();
    const DWORD ebp = r->ebp, st = *(DWORD*)(uintptr_t)(ebp + 8), ctx = *(DWORD*)(uintptr_t)(ebp + 0xC);
    const DWORD d = *(DWORD*)(uintptr_t)(st + 0x140), chunk = *(DWORD*)(uintptr_t)(st + 0x13C);
    v.nr = 0;
    bool ok = McVAdd(v, ebp - MC_VFRAME, MC_VFRAME) && McVAdd(v, st + 0x13C, 0x10) && McVAdd(v, chunk, 0x10) && McVAdd(v, ctx, 0x18);
    if (kind >= 2) {
        const DWORD rec0 = ebp - 0x4C74 + d * 0x130, rm = r->ebx, top = *(DWORD*)(uintptr_t)chunk;
        ok = ok && McVAdd(v, rec0, 0x260) && McVAdd(v, *(DWORD*)(uintptr_t)(rec0 + 0x130), 0x28) &&
             McVAdd(v, *(DWORD*)(uintptr_t)(ebp - 8), 0x28) && McVAdd(v, r->esi - 0x10, 0x18) &&
             McVAdd(v, top, *(const WORD*)(uintptr_t)(rm + 0x38));
        v.rm = rm;
    } else {
        const DWORD inv = r->ecx, rec0 = ebp - 0x4C74 + (d - 1) * 0x130;
        ok = ok && McVAdd(v, rec0, 0x260) && McVAdd(v, inv, 0x28) && McVAdd(v, *(DWORD*)(uintptr_t)rec0, 0x28) &&
             McVAdd(v, *(DWORD*)(uintptr_t)(inv + 8), 8) && McVAdd(v, *(DWORD*)(uintptr_t)(rec0 + 8), 8) &&
             McVAdd(v, r->esi - 0x10, 0x10);
        v.rm = *(DWORD*)(uintptr_t)(inv + 4);
    }
    if (!ok) { MC_CTR.mcVSkipped++; v.state = 0; return; }
    for (int i = 0; i < v.nr; i++) memcpy(v.s0 + v.rg[i].off, v.rg[i].at, v.rg[i].n);
    v.kind = kind, v.r0 = *r, v.ea = MC_EA[kind], v.state = 1;
}

// after the fast writes (regs at the would-be dispatch): record, restore the snapshot, run EA's handler from the entry state
static int __cdecl McVPostC(DWORD* regs) {
    McRegs* r = (McRegs*)regs;
    McV& v = *g_mcv;
    if (GetCurrentThreadId() != g_mcvTid || v.state != 1) return 0;
    if (g_mcvNegative) *(DWORD*)(uintptr_t)(r->ebp - 0x34) ^= 1;  // negative control: a fast-path write EA does not make
    for (int i = 0; i < v.nr; i++) memcpy(v.a + v.rg[i].off, v.rg[i].at, v.rg[i].n);
    v.aEdi = r->edi, v.aEsi = r->esi, v.aEsp = r->esp_;
    for (int i = v.nr - 1; i >= 0; i--) memcpy(v.rg[i].at, v.s0 + v.rg[i].off, v.rg[i].n);
    r->edi = v.r0.edi, r->esi = v.r0.esi, r->eax = r->edx = MC_OPS[v.kind];
    g_mcvJmp = v.ea;
    v.state = 2;
    return 1;
}
static __declspec(naked) void McVPost() {
    __asm {
        pushad
        push esp
        call McVPostC
        add esp, 4
        test eax, eax
        popad
        jnz vp_ea
        movzx eax, word ptr [edi]
        movzx edx, ax
        jmp dword ptr [g_mcHead]
    vp_ea:
        jmp dword ptr [g_mcvJmp]
    }
}

// ves_exec frame slots every reader writes first in its own handler (call / callvirt / calli / newobj before the common
// push, frame return, exception search; tools: docs/139 §8.2 scan) [verified]: dead across a dispatch, so the fast path
// leaves them (they hold EA's temporaries: method, args, this, return slot, cache pair, newobj flag, callee record, chunk)
static bool McScratch(DWORD negOff) {
    switch (negOff) {
    case 0x18: case 0x1C: case 0x24: case 0x28: case 0x34: case 0x48: case 0x50: case 0x54: case 0x58: return true;
    default: return false;
    }
}
static void McVLog(const McV& v, const char* what, DWORD at, DWORD fast, DWORD ea) {
    if (g_mcvLogged++ >= 16) return;
    Log("mintcall verify MISMATCH kind %d rm 0x%08lX ip 0x%08lX: %s at 0x%08lX fast 0x%08lX stock 0x%08lX", v.kind, v.rm, v.r0.edi, what, at, fast, ea);
}
// first dispatch after EA's handler: compare
static void __cdecl McVCheck(DWORD* regs) {
    McRegs* r = (McRegs*)regs;
    McV& v = *g_mcv;
    MC_CTR.mcVerified++;
    bool bad = false;
    if (r->edi != v.aEdi) bad = true, McVLog(v, "edi", 0, v.aEdi, r->edi);
    if (r->esi != v.aEsi) bad = true, McVLog(v, "esi", 0, v.aEsi, r->esi);
    if (r->esp_ != v.aEsp) bad = true, McVLog(v, "esp", 0, v.aEsp, r->esp_);
    for (int i = 0; i < v.nr; i++) {
        const u8* now = v.rg[i].at;
        const u8* fast = v.a + v.rg[i].off;
        for (DWORD k = 0; k < v.rg[i].n; k++)
            if (now[k] != fast[k] && !(i == 0 && McScratch(MC_VFRAME - (k & ~3u)))) {
                const DWORD w = k & ~3u;
                DWORD fw = 0, ew = 0;
                const DWORD m = v.rg[i].n - w < 4 ? v.rg[i].n - w : 4;
                memcpy(&fw, fast + w, m), memcpy(&ew, now + w, m);
                char what[32];
                sprintf_s(what, "range %d +0x%lX", i, w);
                McVLog(v, what, (DWORD)(uintptr_t)(now + w), fw, ew);
                bad = true;
                break;
            }
    }
    if (bad) {
        if (!MC_CTR.mcMismatch) MC_CTR.mcFirstBad = v.kind + 1;
        MC_CTR.mcMismatch++;
    }
    InterlockedExchange(&v.state, 0);
}
static __declspec(naked) void McVCatch() {  // every jump-table entry in verify mode
    __asm {
        pushfd
        cmp g_mcv, 0
        je vc_go
        push eax
        mov eax, g_mcv
        cmp dword ptr [eax], 2          // McV::state
        pop eax
        jne vc_go
        push eax
        mov eax, fs:[0x24]
        cmp eax, g_mcvTid
        pop eax
        jne vc_go
        popfd
        pushad
        push esp
        call McVCheck
        add esp, 4
        popad
        jmp dword ptr [g_mcOrig + edx * 4]
    vc_go:
        popfd
        jmp dword ptr [g_mcOrig + edx * 4]
    }
}

// ---- install ----
static void* const kMcMine[4] = {(void*)McRet, (void*)McRetVoid, (void*)McCall, (void*)McCallvirt};
static DWORD McFnv(const u8* p, DWORD n) {
    DWORD h = 0x811C9DC5;
    for (DWORD i = 0; i < n; i++) h = (h ^ p[i]) * 0x01000193;
    return h;
}
// true when entry `op` currently holds our handler (Satb accepts it as the stock handler and chains to it)
static bool McOwns(DWORD op, DWORD entry) {
    for (int i = 0; i < 4; i++)
        if (MC_OPS[i] == op && entry == (DWORD)(uintptr_t)kMcMine[i]) return true;
    return false;
}
// mode 1 fast (switch from MC_CTR.mcSwitch), 2 verify (every `every`-th eligible call, any thread, one at a time); returns
// the installed mode, 0 off, -1 refused. Before Satb (it chains over our 0xEC entry) and after every code patch elsewhere.
static int McInstall(int mode, DWORD every) {
    MC_CTR.mcReady = 0;
    if (mode <= 0) return 0;
    for (const auto& c : kMcCode)
        if (McFnv((const u8*)(uintptr_t)c.at, c.n) != c.fnv && (!c.fnv2 || McFnv((const u8*)(uintptr_t)c.at, c.n) != c.fnv2)) {
            Log("SKIP mintcall: EA code at 0x%08lX (%lu bytes) is not the stock Steam 1.67 bytes (another hook in the call path)", c.at, c.n);
            MC_CTR.mcReady = -1;
            return -1;
        }
    DWORD* table = (DWORD*)(uintptr_t)MC_TABLE;
    for (int i = 0; i < 4; i++)
        if (table[MC_OPS[i]] != MC_EA[i]) {
            Log("SKIP mintcall: jump-table entry 0x%03lX is 0x%08lX, not the game's 0x%08lX (another hook owns it)", MC_OPS[i], table[MC_OPS[i]], MC_EA[i]);
            MC_CTR.mcReady = -1;
            return -1;
        }
    DWORD mine[MC_NOP];
    memcpy(mine, table, sizeof(mine));
    for (int i = 0; i < 4; i++) mine[MC_OPS[i]] = (DWORD)(uintptr_t)kMcMine[i];
    if (mode >= 2) {
        if (!g_mcv) g_mcv = (McV*)VirtualAlloc(NULL, sizeof(McV), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!g_mcv) { MC_CTR.mcReady = -1; return -1; }
        g_mcvEvery = every ? every : 1, g_mcVerify = 1;
        MC_CTR.mcVerifyEvery = (LONG)g_mcvEvery;
        for (DWORD op = 0; op < MC_NOP; op++) g_mcOrig[op] = (void*)(uintptr_t)mine[op], mine[op] = (DWORD)(uintptr_t)McVCatch;
    }
    if (!Write(table, mine, sizeof(mine))) { MC_CTR.mcReady = -1; return -1; }
    MC_CTR.mcReady = mode >= 2 ? 7 : 3;
    Log("OK   mintcall: call 0xEA, callvirt 0xEC, ret 0x06, ret.void 0x07 -> fast path (%s, switch %ld)%s", mode >= 2 ? "verify" : "fast",
        MC_CTR.mcSwitch, mode >= 2 ? "; every jump-table entry through the verify catch" : "");
    return mode >= 2 ? 2 : 1;
}
// monohost timing: put the four game entries back (true) or ours (false); not in verify mode
#pragma warning(push)
#pragma warning(disable : 4505)  // only monohost calls it
static void McRestoreStock(bool stock) {
    if (g_mcVerify || MC_CTR.mcReady <= 0) return;
    DWORD* table = (DWORD*)(uintptr_t)MC_TABLE;
    DWORD old;
    VirtualProtect(table, MC_NOP * 4, PAGE_EXECUTE_READWRITE, &old);
    for (int i = 0; i < 4; i++) InterlockedExchange((volatile LONG*)&table[MC_OPS[i]], (LONG)(stock ? MC_EA[i] : (DWORD)(uintptr_t)kMcMine[i]));
    VirtualProtect(table, MC_NOP * 4, old, &old);
}
#pragma warning(pop)

// ts3native.ini [MonoFast] CallPath=0 (default) | 1 fast path (in-load A/B: --ab mintcall flips mcSwitch) | 2 verify,
// VerifyEvery=<n> (verify mode: every n-th eligible call runs both ways; default 16). true = installed (keep the log).
static bool ApplyMintCall(const wchar_t* ini) {
    const int path = GetPrivateProfileIntW(L"MonoFast", L"CallPath", 0, ini);
    if (path <= 0) return false;
    MC_CTR.mcSwitch = 1;
    const int every = GetPrivateProfileIntW(L"MonoFast", L"VerifyEvery", 16, ini);
    return McInstall(path >= 2 ? 2 : 1, (DWORD)(every > 0 ? every : 1)) > 0;
}
