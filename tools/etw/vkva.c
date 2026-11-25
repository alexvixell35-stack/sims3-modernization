// 2026-11-20, docs/215 G1: address space a 32-bit process pays for the Vulkan drivers, the way Apex's d3d9 uses them
// (instance, enumerate, one device on the NVIDIA GPU). Run it with and without VK_LOADER_DRIVERS_DISABLE=*amd* at Medium
// integrity (the loader ignores these variables in elevated processes). Prints the used address space by type per step.
#include <windows.h>
#include <stdio.h>
#include <string.h>

typedef int (__stdcall* PFN_CI)(const void*, const void*, void**);
typedef int (__stdcall* PFN_EPD)(void*, unsigned*, void**);
typedef void (__stdcall* PFN_GPDP)(void*, void*);
typedef int (__stdcall* PFN_CD)(void*, const void*, const void*, void**);
typedef void (__stdcall* PFN_DD)(void*, const void*);
typedef void (__stdcall* PFN_DI)(void*, const void*);

static void Va(const char* step) {
    double priv = 0, map = 0, img = 0, res = 0;
    MEMORY_BASIC_INFORMATION mi;
    for (unsigned char* p = 0; VirtualQuery(p, &mi, sizeof mi) == sizeof mi; p = (unsigned char*)mi.BaseAddress + mi.RegionSize) {
        double mb = mi.RegionSize / 1048576.0;
        if (mi.State == MEM_RESERVE) res += mb;
        if (mi.State != MEM_FREE) {
            if (mi.Type == MEM_PRIVATE) priv += mb; else if (mi.Type == MEM_MAPPED) map += mb; else if (mi.Type == MEM_IMAGE) img += mb;
        }
        if ((size_t)mi.BaseAddress + mi.RegionSize < (size_t)mi.BaseAddress) break;
    }
    printf("%-22s used %7.1f MB: private %7.1f map %6.1f image %6.1f (reserve-only %6.1f)\n", step, priv + map + img, priv, map, img, res);
}

int main(void) {
    Va("start");
    HMODULE vk = LoadLibraryA("vulkan-1.dll");
    if (!vk) { printf("no vulkan-1.dll\n"); return 1; }
    PFN_CI ci = (PFN_CI)GetProcAddress(vk, "vkCreateInstance");
    PFN_EPD epd = (PFN_EPD)GetProcAddress(vk, "vkEnumeratePhysicalDevices");
    PFN_GPDP gpdp = (PFN_GPDP)GetProcAddress(vk, "vkGetPhysicalDeviceProperties");
    PFN_CD cd = (PFN_CD)GetProcAddress(vk, "vkCreateDevice");
    PFN_DD dd = (PFN_DD)GetProcAddress(vk, "vkDestroyDevice");
    PFN_DI di = (PFN_DI)GetProcAddress(vk, "vkDestroyInstance");
    Va("loader loaded");
    struct { int sType; const void* pNext; const char* app; unsigned av; const char* eng; unsigned ev; unsigned api; } app = { 0, 0, "vkva", 1, "none", 1, (1u << 22) | (1u << 12) };
    struct { int sType; const void* pNext; unsigned flags; const void* app; unsigned nl; const void* l; unsigned ne; const void* e; } ici = { 1, 0, 0, &app, 0, 0, 0, 0 };
    void* inst = 0;
    int r = ci(&ici, 0, &inst);
    Va("instance");
    if (r) { printf("vkCreateInstance %d\n", r); return 1; }
    unsigned n = 8; void* devs[8];
    epd(inst, &n, devs);
    Va("enumerated");
    {   // images >= 4 MB after the instance (which ICDs the loader brought in)
        MEMORY_BASIC_INFORMATION mi; char name[MAX_PATH]; void* last = 0;
        for (unsigned char* p = 0; VirtualQuery(p, &mi, sizeof mi) == sizeof mi; p = (unsigned char*)mi.BaseAddress + mi.RegionSize) {
            if (mi.Type == MEM_IMAGE && mi.AllocationBase != last) {
                last = mi.AllocationBase;
                MEMORY_BASIC_INFORMATION m2; size_t sz = 0;
                for (unsigned char* q = (unsigned char*)mi.AllocationBase; VirtualQuery(q, &m2, sizeof m2) == sizeof m2 && m2.AllocationBase == mi.AllocationBase; q += m2.RegionSize) sz += m2.RegionSize;
                if (sz >= (4u << 20) && GetModuleFileNameA((HMODULE)mi.AllocationBase, name, sizeof name))
                    printf("  image %6.1f MB %s\n", sz / 1048576.0, strrchr(name, '\\') ? strrchr(name, '\\') + 1 : name);
            }
            if ((size_t)mi.BaseAddress + mi.RegionSize < (size_t)mi.BaseAddress) break;
        }
    }
    void* nv = 0;
    static unsigned char props[8192];
    for (unsigned i = 0; i < n; i++) {
        gpdp(devs[i], props);
        printf("device %u: vendor 0x%04X %s\n", i, *(unsigned*)(props + 8), (char*)(props + 20));
        if (*(unsigned*)(props + 8) == 0x10DE) nv = devs[i];
    }
    if (nv) {
        float prio = 1.0f;
        struct { int sType; const void* pNext; unsigned flags; unsigned qf; unsigned qc; const float* pr; } qci = { 2, 0, 0, 0, 1, &prio };
        struct { int sType; const void* pNext; unsigned flags; unsigned nq; const void* q; unsigned nl; const void* l; unsigned ne; const void* e; const void* f; } dci = { 3, 0, 0, 1, &qci, 0, 0, 0, 0, 0 };
        void* dev = 0;
        r = cd(nv, &dci, 0, &dev);
        Va("NVIDIA device");
        if (!r) dd(dev, 0);
    }
    di(inst, 0);
    Va("destroyed");
    return 0;
}
