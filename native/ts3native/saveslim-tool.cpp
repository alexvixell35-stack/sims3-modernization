// The [SaveSlim] job (saveslim.cpp, docs/15 phase 11) on one save folder, outside the game:
//   saveslim-tool.exe <save folder> [mode]
// Mode 0 (default) reads only and prints what Mode 2 would drop. Modes 1 and 2 rewrite the folder's ExportDB packages in
// place, as the game's worker does after a save: run them only on a copy of a save, with the game closed.
#define TS3NATIVE_NO_DLLMAIN
#include "ts3native.cpp"

int wmain(int argc, wchar_t** argv) {
    if (argc < 2 || argc > 3) { printf("usage: saveslim-tool.exe <save folder> [0|1|2]\n"); return 2; }
    const int mode = argc == 3 ? _wtoi(argv[2]) : 0;
    if (mode < 0 || mode > 2) { printf("mode must be 0, 1 or 2\n"); return 2; }
    wchar_t tmp[MAX_PATH];
    if (!Join(tmp, argv[1], L"..\ts3native-slim.tmp")) return 2;  // beside the folder: same volume
    QueryPerformanceFrequency(&g_qpf);
    g_log = GetStdHandle(STD_OUTPUT_HANDLE);  // Log() lines to the console
    SlimStats s;
    SlimRun(argv[1], tmp, mode, &s);
    SlimLog(s);
    return s.fail ? 1 : 0;
}
