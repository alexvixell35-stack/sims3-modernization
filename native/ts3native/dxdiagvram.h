// DxDiag VRAM sidecar (docs/284): shared by ts3native.asi ([Startup] DxDiagCache, dxdiagcache.cpp) and the helper
// dxdiag-vram.exe (harness setup). Standalone: windows.h + COM only, no ts3native globals.
//
// TS3W's VRAM_DxDiag 0x612640 (docs/37 §6, docs/284 §2) [verified, Steam 1.67 disassembly]: CoInitialize(NULL);
// CoCreateInstance(CLSID_DxDiagProvider, INPROC); Initialize({16, 0x6F, FALSE, NULL}); root -> "DxDiag_DisplayDevices";
// for each child container (EnumChildContainerNames 256 chars, GetChildContainer): GUID = GUID_NULL, then
// IIDFromString(szDeviceIdentifier) when that prop is a BSTR (result not checked); on a match with the D3D9 adapter's
// DeviceIdentifier (+0x438): szDisplayMemoryEnglish (BSTR, narrowed, non-empty, _atoflt ok) -> MB = truncated float,
// value = ((MB + 4) << 20 & 0xFF800000) >> 20, and the loop stops at that first match even when the memory string is
// unusable (value 0). No match, or any COM step failing: 0. CoUninitialize at the end.
// The sidecar keeps the raw strings of every display device, so the asi replays that decision without DxDiag.
#pragma once
#include <windows.h>
#include <dxdiag.h>
#include <stdio.h>
#include <stdlib.h>
#include <float.h>
#undef small  // rpcndr.h (via dxdiag.h) defines it as char; castrim.cpp has a local named small
#pragma comment(lib, "ole32.lib")     // TS3W.exe already imports ole32 and oleaut32: no new DLL loads in the game
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "dxguid.lib")    // CLSID_DxDiagProvider, IID_IDxDiagProvider (static GUID data only)
#pragma comment(lib, "advapi32.lib")

struct DdcDev { wchar_t guid[64]; wchar_t mem[64]; bool guidBstr; };  // guidBstr 0: the game compares GUID_NULL
enum { DDC_MAX = 16 };

// The game's memory string -> MB value (pure; the selftest drives it). Narrowing: the game uses its own UTF-16 -> 8-bit
// converter; digits and "MB" are ASCII either way. shortcut: WideCharToMultiByte(CP_ACP), exact for ASCII strings.
static DWORD DdcValueFromMem(const wchar_t* mem) {
    char s[80];
    if (!mem || !*mem || !WideCharToMultiByte(CP_ACP, 0, mem, -1, s, sizeof(s), NULL, NULL) || !*s) return 0;
    _CRT_FLOAT f;
    if (_atoflt(&f, s)) return 0;  // overflow / underflow: the game keeps 0 and stops
    // 0x406800: fist (round to nearest) then minus 1 when that rounded up: truncation toward zero for positive values
    const int mb = (int)f.f;
    return ((((DWORD)mb + 4) << 20) & 0xFF800000u) >> 20;
}

// First device whose GUID equals id decides (the game's loop stops there); none: 0.
static DWORD DdcResolve(const DdcDev* d, int n, const GUID* id) {
    for (int i = 0; i < n; i++) {
        GUID g = GUID_NULL;
        if (d[i].guidBstr) IIDFromString(d[i].guid, &g);  // result ignored, like the game
        if (memcmp(&g, id, sizeof(GUID)) == 0) return DdcValueFromMem(d[i].mem);
    }
    return 0;
}

static void DdcBstr(IDxDiagContainer* c, const wchar_t* prop, wchar_t* out, size_t cap, bool* isBstr) {
    VARIANT v;
    VariantInit(&v);
    *out = 0;
    if (isBstr) *isBstr = false;
    if (SUCCEEDED(c->GetProp(prop, &v)) && v.vt == VT_BSTR) {
        if (isBstr) *isBstr = true;
        wcsncpy_s(out, cap, v.bstrVal ? v.bstrVal : L"", _TRUNCATE);
    }
    VariantClear(&v);
}

// One real DxDiag query with the game's arguments (COM already initialised by the caller). false: a step the game also
// fails on (its answer would be 0); *n devices in DxDiag order.
static bool DdcEnum(DdcDev* d, int* n) {
    *n = 0;
    IDxDiagProvider* p = NULL;
    if (FAILED(CoCreateInstance(CLSID_DxDiagProvider, NULL, CLSCTX_INPROC_SERVER, IID_IDxDiagProvider, (void**)&p))) return false;
    DXDIAG_INIT_PARAMS ip = {sizeof(DXDIAG_INIT_PARAMS), DXDIAG_DX9_SDK_VERSION, FALSE, NULL};
    IDxDiagContainer *root = NULL, *devs = NULL;
    bool ok = false;
    DWORD count = 0;
    if (SUCCEEDED(p->Initialize(&ip)) && SUCCEEDED(p->GetRootContainer(&root)) &&
        SUCCEEDED(root->GetChildContainer(L"DxDiag_DisplayDevices", &devs)) && SUCCEEDED(devs->GetNumberOfChildContainers(&count))) {
        ok = true;
        for (DWORD i = 0; i < count && *n < DDC_MAX; i++) {
            wchar_t name[256];
            IDxDiagContainer* c = NULL;
            if (FAILED(devs->EnumChildContainerNames(i, name, 256)) || FAILED(devs->GetChildContainer(name, &c))) continue;  // game: skipped
            DdcDev& e = d[(*n)++];
            DdcBstr(c, L"szDeviceIdentifier", e.guid, 64, &e.guidBstr);
            DdcBstr(c, L"szDisplayMemoryEnglish", e.mem, 64, NULL);
            c->Release();
        }
    }
    if (devs) devs->Release();
    if (root) root->Release();
    p->Release();
    return ok;
}

// Cheap machine key, no WMI, no D3D: every display-class driver (MatchingDeviceId = PCI vendor/device/subsys/rev,
// DriverVersion, DriverDate), installed RAM in 256 MB steps (WDDM's DxDiag memory = dedicated + shared, shared follows
// RAM) and the Windows build. A changed GPU, driver, RAM or OS build gives another key: the sidecar is probed again.
// Not GetAdapterIdentifier: under the RTX Remix bridge the game's adapter is the bridge's fixed identity, which does not
// change with the real driver.
static unsigned __int64 DdcFnv(unsigned __int64 h, const void* p, size_t n) {
    for (size_t i = 0; i < n; i++) { h ^= ((const BYTE*)p)[i]; h *= 1099511628211ull; }
    return h;
}
static void DdcSig(wchar_t* out, size_t cap) {
    unsigned __int64 h = 14695981039346656037ull;
    HKEY cls;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}", 0,
                      KEY_READ, &cls) == ERROR_SUCCESS) {
        for (DWORD i = 0;; i++) {
            wchar_t sub[64];
            DWORD sl = 64;
            if (RegEnumKeyExW(cls, i, sub, &sl, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;
            static const wchar_t* const vals[] = {L"MatchingDeviceId", L"DriverVersion", L"DriverDate"};
            for (const wchar_t* v : vals) {
                wchar_t buf[256] = L"";
                DWORD bl = sizeof(buf) - sizeof(wchar_t);
                if (RegGetValueW(cls, sub, v, RRF_RT_REG_SZ, NULL, buf, &bl) == ERROR_SUCCESS) h = DdcFnv(h, buf, wcslen(buf) * 2);
                h = DdcFnv(h, L"|", 2);
            }
        }
        RegCloseKey(cls);
    }
    MEMORYSTATUSEX ms = {sizeof(ms)};
    if (GlobalMemoryStatusEx(&ms)) { const unsigned __int64 q = ms.ullTotalPhys >> 28; h = DdcFnv(h, &q, sizeof(q)); }
    wchar_t build[32] = L"";
    DWORD bl = sizeof(build) - sizeof(wchar_t), ubr = 0, ul = sizeof(ubr);
    RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", L"CurrentBuild", RRF_RT_REG_SZ, NULL, build, &bl);
    RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", L"UBR", RRF_RT_REG_DWORD, NULL, &ubr, &ul);
    h = DdcFnv(DdcFnv(h, build, wcslen(build) * 2), &ubr, sizeof(ubr));
    swprintf_s(out, cap, L"%016llX", h);
}

// Sidecar <dir>\ts3native-vram.ini (never in a save): [DxDiag] Sig, ProbeMs, By, Devices=N, Dev<i>=<guid>|<mem>
// (guid "-" = not a BSTR). Read: true only when Sig equals sig and every row parses.
static bool DdcRead(const wchar_t* path, const wchar_t* sig, DdcDev* d, int* n, DWORD* probeMs, wchar_t* by, size_t byCap) {
    wchar_t s[64];
    GetPrivateProfileStringW(L"DxDiag", L"Sig", L"", s, 64, path);
    if (!*s || wcscmp(s, sig)) return false;
    const int cnt = (int)GetPrivateProfileIntW(L"DxDiag", L"Devices", -1, path);
    if (cnt < 0 || cnt > DDC_MAX) return false;
    for (int i = 0; i < cnt; i++) {
        wchar_t key[16], row[160];
        swprintf_s(key, L"Dev%d", i);
        GetPrivateProfileStringW(L"DxDiag", key, L"", row, 160, path);
        wchar_t* bar = wcschr(row, L'|');
        if (!bar) return false;
        *bar = 0;
        d[i].guidBstr = wcscmp(row, L"-") != 0;
        wcsncpy_s(d[i].guid, d[i].guidBstr ? row : L"", _TRUNCATE);
        wcsncpy_s(d[i].mem, bar + 1, _TRUNCATE);
    }
    *n = cnt;
    *probeMs = GetPrivateProfileIntW(L"DxDiag", L"ProbeMs", 0, path);
    GetPrivateProfileStringW(L"DxDiag", L"By", L"?", by, (DWORD)byCap, path);
    return true;
}

// Rows first, Sig last: a write cut short leaves no Sig, so the next start probes again.
static bool DdcWrite(const wchar_t* path, const wchar_t* sig, const DdcDev* d, int n, DWORD probeMs, const wchar_t* by) {
    WritePrivateProfileStringW(L"DxDiag", L"Sig", NULL, path);  // fails harmlessly when the file is new
    bool ok = true;
    wchar_t v[160], key[16];
    for (int i = 0; i < n; i++) {
        swprintf_s(key, L"Dev%d", i);
        swprintf_s(v, L"%s|%s", d[i].guidBstr ? d[i].guid : L"-", d[i].mem);
        ok = WritePrivateProfileStringW(L"DxDiag", key, v, path) && ok;
    }
    swprintf_s(v, L"%d", n);
    ok = WritePrivateProfileStringW(L"DxDiag", L"Devices", v, path) && ok;
    swprintf_s(v, L"%lu", probeMs);
    ok = WritePrivateProfileStringW(L"DxDiag", L"ProbeMs", v, path) && ok;
    ok = WritePrivateProfileStringW(L"DxDiag", L"By", by, path) && ok;
    return WritePrivateProfileStringW(L"DxDiag", L"Sig", sig, path) && ok;
}
