// [MemDiag] AllocSitesOwners owner range table (docs/190 phase 3, §5.6), included by allocsites.cpp. Data, not hook sites:
// tools/hook-sites.ts scans the .cpp files only, and these TS3W.exe addresses are range bounds no module patches.
enum : u8 { OW_EXEQ, OW_CORE, OW_RES, OW_CONTENT, OW_UI, OW_ANIM, OW_STR, OW_CAS, OW_GFX, OW_FX, OW_TREE, OW_PHYS, OW_ROUTE,
            OW_SCRIPT, OW_MONO, OW_GC, OW_AUDIO, OW_OBJ, OW_WORLD, OW_ONLINE, OW_VIDEO, OW_LIB,  // TS3W.exe
            OW_OURS, OW_APEX, OW_S3SS, OW_S3PP, OW_D3D9, OW_DRV, OW_SYS, OW_MOD, OW_NONE, OW_N };   // modules, no frame
static const char* const kAoName[OW_N] = {"exe?", "core", "res", "content", "ui", "anim", "str", "cas", "gfx", "fx", "tree",
    "phys", "route", "script", "mono", "gc", "audio", "obj", "world", "online", "video", "lib", "ours", "apex", "s3ss", "s3pp",
    "d3d9", "drv", "sys", "mod", "none"};
struct AsOwnRow { DWORD lo, hi; u8 own, ver; };
static const AsOwnRow kAsOwn[] = {  // sorted, half-open; the EA allocator 0x4E3D00-0x4E9000 is not a row (its from= owns it)
    {0x401000, 0x414000, OW_CORE, 1},   {0x42D000, 0x442000, OW_LIB, 1},    {0x442000, 0x468000, OW_UI, 1},
    {0x49B000, 0x49F000, OW_UI, 0},     {0x4A0000, 0x4B4000, OW_RES, 1},    {0x4B4000, 0x4BC000, OW_UI, 0},
    {0x4BC000, 0x4D8000, OW_CORE, 0},   {0x4D8000, 0x4E0000, OW_RES, 1},    {0x4E0000, 0x4E3D00, OW_CORE, 1},
    {0x4E9000, 0x4EB000, OW_UI, 0},     {0x4EB000, 0x4ED000, OW_RES, 1},    {0x4ED000, 0x500000, OW_UI, 1},
    {0x500000, 0x54C000, OW_UI, 1},     {0x54C000, 0x584000, OW_ANIM, 1},   {0x584000, 0x59AF50, OW_CORE, 1},
    {0x59AF50, 0x5A0000, OW_STR, 0},    {0x5A0000, 0x5A2C00, OW_CORE, 0},   {0x5A2C00, 0x60AA00, OW_CAS, 1},
    {0x60AA00, 0x61B000, OW_GFX, 1},    {0x61B000, 0x676F70, OW_ROUTE, 1},  {0x676F70, 0x688000, OW_PHYS, 1},
    {0x688000, 0x6CBF90, OW_GFX, 1},    {0x6CBF90, 0x6CE300, OW_CAS, 1},    {0x6CE300, 0x70D990, OW_GFX, 1},
    {0x70D990, 0x718620, OW_TREE, 1},   {0x718620, 0x723DF0, OW_GFX, 0},    {0x723DF0, 0x724810, OW_UI, 0},
    {0x724810, 0x726000, OW_GFX, 0},    {0x726000, 0x73C000, OW_RES, 1},    {0x73C000, 0x750000, OW_SCRIPT, 0},
    {0x750000, 0x768000, OW_ROUTE, 1},  {0x768000, 0x779470, OW_SCRIPT, 1}, {0x779470, 0x783000, OW_UI, 0},
    {0x783000, 0x799000, OW_SCRIPT, 0}, {0x799000, 0x7A0F40, OW_GFX, 1},    {0x7A0F40, 0x7C4450, OW_TREE, 1},
    {0x7C4450, 0x7C8000, OW_ONLINE, 1}, {0x7C8000, 0x7DC000, OW_CORE, 0},   {0x7DC000, 0x7F4000, OW_UI, 1},
    {0x7F4000, 0x808000, OW_ANIM, 1},   {0x808000, 0x860000, OW_ROUTE, 1},  {0x860000, 0x870000, OW_AUDIO, 1},
    {0x870000, 0x89F000, OW_ONLINE, 1}, {0x89F000, 0x8C0000, OW_AUDIO, 1},  {0x8C0000, 0x930000, OW_FX, 0},
    {0x930000, 0x95BA60, OW_OBJ, 0},    {0x95BA60, 0x960000, OW_TREE, 1},   {0x960000, 0x980000, OW_CORE, 0},
    {0x980000, 0x9D8000, OW_CONTENT, 1}, {0x9D8000, 0x9E4000, OW_VIDEO, 1}, {0x9E4000, 0xA44000, OW_ONLINE, 1},
    {0xA44000, 0xD4B000, OW_WORLD, 1},  {0xD4B000, 0xD65800, OW_UI, 1},     {0xD65800, 0xD70000, OW_WORLD, 0},
    {0xD70000, 0xD70900, OW_SCRIPT, 0}, {0xD70900, 0xD70A10, OW_GC, 1},     {0xD70A10, 0xD70A40, OW_MONO, 1},
    {0xD70A40, 0xD82000, OW_SCRIPT, 1}, {0xD82000, 0xDC0000, OW_MONO, 0},   {0xE20000, 0xE4A000, OW_MONO, 0},
    {0xE4A000, 0xE79000, OW_GC, 1},     {0xE79000, 0xEC0000, OW_MONO, 0},   {0xEC0000, 0xED7000, OW_CORE, 0},
    {0xED7000, 0xF01000, OW_AUDIO, 1},  {0xF1C800, 0xF1D000, OW_VIDEO, 0},  {0xF48000, 0xF6FA00, OW_ONLINE, 0},
    {0xF6FA00, 0xF95000, OW_LIB, 0}};
static const int kAsOwnN = (int)(sizeof(kAsOwn) / sizeof(kAsOwn[0]));
