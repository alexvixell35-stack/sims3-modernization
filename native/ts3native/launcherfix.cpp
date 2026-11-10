// [LauncherFix] (docs/255): EA launcher bug, Steam 1.67. Sims3LauncherW.exe (managed) runs its business logic in
// TSLHost.dll. The telemetry job (TSLHost 0x1003FCB0, its vtable job run) starts an upload of
// <Documents>\Electronic Arts\The Sims 3\Sims3Logs.xml (0x10034450 -> 0x10032CF0) and then busy-waits, with no sleep,
// until that file is gone: `while (!job.cancel && FileExists(Sims3Logs.xml)) pump();` [verified, static]. The file is only
// deleted after a finished upload (0x10034170); EA's telemetry server is gone, so the upload never finishes and the job
// spins one core forever, which keeps the launcher process alive after its window closes [inferred: no live trace yet].
// Denying access to the file makes the upload's open fail (0x10032790 returns 0, 0x10032CF0 returns 0x28), so the job
// never enters the wait: the community workaround [community, tester foulplay].
// Fix: the call to 0x10032CF0 at 0x10034535 becomes `mov eax,0x20`, the existing "file over 512 KB, skipped" result:
// no upload starts and the job returns at once. Launcher host only, byte-checked, no threads, nothing at detach.

static const DWORD kLfTslTimestamp = 0x52DEC86A;  // TSLHost.dll of Steam 1.67 (launcher 52DEC865)
static const DWORD kLfSiteRva = 0x34535;  // 0x10034535 at the preferred base 0x10000000 (rel32 call: no relocation)
static const u8 kLfOrig[8] = {0x50, 0x8B, 0xCE, 0xE8, 0xB3, 0xE7, 0xFF, 0xFF};  // push eax; mov ecx,esi; call 0x10032CF0
static const u8 kLfNew[8] = {0xB8, 0x20, 0x00, 0x00, 0x00, 0x90, 0x90, 0x90};   // mov eax,20h; nop x3 (callee did ret 4)
static bool g_lfDone;

// 1 = patched now, 0 = already patched, -1 = bytes differ (nothing written), -2 = write failed
static int LfPatchSite(u8* site) {
    if (Matches(site, kLfNew, sizeof(kLfNew))) return 0;
    if (!Matches(site, kLfOrig, sizeof(kLfOrig))) return -1;
    return Write(site, kLfNew, sizeof(kLfNew)) ? 1 : -2;
}

static void LfPatchTsl(HMODULE tsl) {
    if (g_lfDone) return;
    g_lfDone = true;
    const u8* b = (const u8*)tsl;
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(b + ((const IMAGE_DOS_HEADER*)b)->e_lfanew);
    const DWORD ts = nt->FileHeader.TimeDateStamp, size = nt->OptionalHeader.SizeOfImage;
    if (ts != kLfTslTimestamp) {  // + the site's 8 bytes below
        Log("SKIP launcher fix: unknown TSLHost.dll build (pe %08lX size 0x%lX), nothing written", ts, size);
        return;
    }
    char hex[32];
    switch (LfPatchSite((u8*)b + kLfSiteRva)) {
        case 1: Log("OK   launcher fix: TSLHost.dll+0x%X telemetry upload skipped (the job no longer waits for Sims3Logs.xml)", kLfSiteRva); break;
        case 0: Log("OK   launcher fix: TSLHost.dll+0x%X already patched", kLfSiteRva); break;
        case -1:
            Hex(b + kLfSiteRva, sizeof(kLfOrig), hex, sizeof(hex));
            Log("SKIP launcher fix: bytes at TSLHost.dll+0x%X differ (found %s), nothing written", kLfSiteRva, hex);
            break;
        default: Log("FAIL launcher fix: VirtualProtect at TSLHost.dll+0x%X failed (err %lu)", kLfSiteRva, GetLastError());
    }
}

// LdrRegisterDllNotification (ntdll, Vista+): TSLHost.dll loads after us when the CLR binds its first P/Invoke. The
// callback runs under the loader lock after the image is mapped and relocated, before its DllMain: patch there.
struct LfUnicode { USHORT Length, MaximumLength; PCWSTR Buffer; };
struct LfNotifyData { ULONG Flags; const LfUnicode* FullDllName; const LfUnicode* BaseDllName; PVOID DllBase; ULONG SizeOfImage; };
typedef VOID(CALLBACK* LfNotifyFn)(ULONG reason, const LfNotifyData* data, PVOID ctx);
typedef LONG(NTAPI* LfRegisterFn)(ULONG flags, LfNotifyFn fn, PVOID ctx, PVOID* cookie);

static bool LfIsTsl(const LfUnicode* n) {
    static const wchar_t kName[] = L"TSLHost.dll";
    const USHORT len = (USHORT)(wcslen(kName) * sizeof(wchar_t));
    return n && n->Buffer && n->Length == len && _wcsnicmp(n->Buffer, kName, len / sizeof(wchar_t)) == 0;
}

static VOID CALLBACK LfOnDll(ULONG reason, const LfNotifyData* data, PVOID) {
    if (reason == 1 && data && LfIsTsl(data->BaseDllName)) LfPatchTsl((HMODULE)data->DllBase);  // 1 = LOADED
}

// Launcher host only (Run checked the exe name). Steam 1.67 Sims3LauncherW.exe = pe 52DEC865; the EA app's
// Sims3Launcher.exe (1.69, pe 67072CED) also runs a TSLHost build we have not read: log and do nothing.
// Returns true while it waits for TSLHost.dll (the log stays open for the callback's line).
static bool ApplyLauncherFix(const wchar_t* ini, DWORD exeTs) {
    if (!GetPrivateProfileIntW(L"LauncherFix", L"Enabled", 1, ini)) { Log("launcher fix disabled (Enabled=0)"); return false; }
    if (exeTs != 0x52DEC865) { Log("SKIP launcher fix: unknown launcher build (pe %08lX), nothing written", exeTs); return false; }
    if (HMODULE tsl = GetModuleHandleW(L"TSLHost.dll")) { LfPatchTsl(tsl); return false; }
    LfRegisterFn reg = (LfRegisterFn)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "LdrRegisterDllNotification");
    PVOID cookie = NULL;
    const LONG st = reg ? reg(0, LfOnDll, NULL, &cookie) : -1;
    if (st < 0) { Log("FAIL launcher fix: LdrRegisterDllNotification (status 0x%08lX)", (DWORD)st); return false; }
    Log("launcher fix: waiting for TSLHost.dll to load");
    return true;
}
