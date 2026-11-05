// One-off measurement proxy (not a player feature): d3d9.dll that forwards Direct3DCreate9 / Direct3DCreate9Ex to the
// system d3d9.dll's Direct3DCreate9On12 / Direct3DCreate9On12Ex with Enable9On12 = TRUE (DX9 straight to DX12).
// Logs the path taken to d3d9on12-proxy.log beside this dll ("path=9On12" or "FELL BACK ..."); queue.ts d3d9Dll jobs
// FAIL on a fall back. 32-bit, no deps. Build: node tools/d3d9on12-test/build.ts
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>

struct D3D9ON12_ARGS { BOOL Enable9On12; void* pD3D12Device; void* ppD3D12Queues[2]; UINT NumQueues; UINT NodeMask; };
typedef void* (WINAPI* Create9_t)(UINT);
typedef void* (WINAPI* Create9On12_t)(UINT, D3D9ON12_ARGS*, UINT);
typedef HRESULT (WINAPI* Create9Ex_t)(UINT, void**);
typedef HRESULT (WINAPI* Create9On12Ex_t)(UINT, D3D9ON12_ARGS*, UINT, void**);

static HMODULE g_sys;
static char g_log[MAX_PATH];

static void Log(const char* fmt, ...) {
  char buf[512];
  va_list ap; va_start(ap, fmt); vsnprintf(buf, sizeof buf - 3, fmt, ap); va_end(ap);
  strcat_s(buf, "\r\n");
  HANDLE h = CreateFileA(g_log, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_ALWAYS, 0, 0);
  if (h == INVALID_HANDLE_VALUE) return;
  DWORD n; WriteFile(h, buf, (DWORD)strlen(buf), &n, 0); CloseHandle(h);
}

static HMODULE Sys() { // system d3d9.dll by full path, never ourselves
  if (g_sys) return g_sys;
  char p[MAX_PATH]; GetSystemDirectoryA(p, MAX_PATH); strcat_s(p, "\\d3d9.dll");
  g_sys = LoadLibraryA(p);
  Log("system d3d9: %s -> %s", p, g_sys ? "loaded" : "LOAD FAILED");
  return g_sys;
}

extern "C" void* WINAPI Direct3DCreate9(UINT sdk) {
  HMODULE m = Sys(); if (!m) return 0;
  D3D9ON12_ARGS a = {}; a.Enable9On12 = TRUE;
  auto on12 = (Create9On12_t)GetProcAddress(m, "Direct3DCreate9On12");
  void* r = on12 ? on12(sdk, &a, 1) : 0;
  if (r) { Log("Direct3DCreate9 path=9On12 ok"); return r; }
  auto plain = (Create9_t)GetProcAddress(m, "Direct3DCreate9");
  Log("Direct3DCreate9 FELL BACK to plain d3d9 (On12 export %s)", on12 ? "returned NULL" : "missing");
  return plain ? plain(sdk) : 0;
}

extern "C" HRESULT WINAPI Direct3DCreate9Ex(UINT sdk, void** out) {
  HMODULE m = Sys(); if (!m) return E_FAIL;
  D3D9ON12_ARGS a = {}; a.Enable9On12 = TRUE;
  auto on12 = (Create9On12Ex_t)GetProcAddress(m, "Direct3DCreate9On12Ex");
  HRESULT hr = on12 ? on12(sdk, &a, 1, out) : E_NOTIMPL;
  if (SUCCEEDED(hr)) { Log("Direct3DCreate9Ex path=9On12 ok"); return hr; }
  auto plain = (Create9Ex_t)GetProcAddress(m, "Direct3DCreate9Ex");
  Log("Direct3DCreate9Ex FELL BACK to plain d3d9 (On12Ex hr=0x%08lx)", (unsigned long)hr);
  return plain ? plain(sdk, out) : E_FAIL;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD why, LPVOID) {
  if (why == DLL_PROCESS_ATTACH) {
    DisableThreadLibraryCalls(h);
    GetModuleFileNameA(h, g_log, MAX_PATH);
    char* s = strrchr(g_log, '\\'); if (s) *(s + 1) = 0;
    strcat_s(g_log, "d3d9on12-proxy.log");
    DeleteFileA(g_log); // fresh per game start
    Log("proxy loaded");
  }
  return TRUE;
}
