// dxdiag-vram.exe <Bin dir> [--force] [--check] (docs/284): writes <Bin dir>\ts3native-vram.ini, the sidecar of
// [Startup] DxDiagCache, from one DxDiag query outside the game (same calls and arguments as TS3W's VRAM_DxDiag, so the
// asi's replay gives the game's own answer). Harness setup and turbo-compat Bin states run it, so no test launch pays
// the ~60 s query or starts COM's ping-loop worker. A sidecar whose machine key (display drivers, RAM, Windows build)
// still matches is kept unless --force. --check: exit 0 when current, 3 when it would probe. 32-bit like the game.
// Exit: 0 current or written, 1 usage, 2 query or write failed, 3 (--check) stale or missing.
#include "dxdiagvram.h"

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) { fwprintf(stderr, L"usage: dxdiag-vram.exe <Bin dir> [--force] [--check]\n"); return 1; }
    bool force = false, check = false;
    for (int i = 2; i < argc; i++) force |= !wcscmp(argv[i], L"--force"), check |= !wcscmp(argv[i], L"--check");
    wchar_t path[MAX_PATH], sig[24], by[16];
    swprintf_s(path, L"%s\\ts3native-vram.ini", argv[1]);
    DdcSig(sig, 24);
    DdcDev d[DDC_MAX];
    int n = 0;
    DWORD ms = 0;
    if (!force && DdcRead(path, sig, d, &n, &ms, by, 16)) {
        wprintf(L"dxdiag-vram: current (sig %s, %d device(s), probe %lu ms by %s)\n", sig, n, ms, by);
        return 0;
    }
    if (check) { wprintf(L"dxdiag-vram: stale or missing (sig %s)\n", sig); return 3; }
    const ULONGLONG t0 = GetTickCount64();
    CoInitialize(NULL);
    const bool ok = DdcEnum(d, &n);
    CoUninitialize();
    ms = (DWORD)(GetTickCount64() - t0);
    if (!ok) { wprintf(L"dxdiag-vram: DxDiag query failed after %lu ms, nothing written\n", ms); return 2; }
    if (!DdcWrite(path, sig, d, n, ms, L"helper")) { wprintf(L"dxdiag-vram: write %s failed (err %lu)\n", path, GetLastError()); return 2; }
    for (int i = 0; i < n; i++) wprintf(L"dxdiag-vram: device %d %s memory \"%s\" -> %lu MB if matched\n", i, d[i].guid, d[i].mem, DdcValueFromMem(d[i].mem));
    wprintf(L"dxdiag-vram: wrote ts3native-vram.ini (sig %s, %d device(s), probe %lu ms)\n", sig, n, ms);
    return 0;
}
