// [StreamBuf] (docs/547): the data buffers of EA's IO memory streams (EA::IO::MemoryStream, vtable 0xFB5BCC) counted by
// the code that sized the stream (Stats) and served from one arena of our own (Pool). OFF by default
// (Enabled=0). Included by ts3native.cpp after allocsites.cpp (uses its AsCallBefore / g_asTextLo / g_asTextHi);
// memdiag.cpp calls StbMemLine at every "diag: mem" mark.
//
// What the game does [verified disassembly TS3W 1.67, sha256 b78a3d53..., 2026-11-11; docs/547 §2-§4]:
// - A MemoryStream (0x28 bytes: +4 SharedPointer*, +8 allocation name, +0xC refcount, +0x10 size, +0x14 capacity, +0x18
//   position, +0x1C resize enabled, +0x20 float resize factor, +0x24 resize increment) keeps its bytes in a refcounted
//   SharedPointer (0x14 bytes, vtable 0xFB84D0: +4 allocator, +8 data, +0xC refcount, +0x10 "free the data" byte).
// - MemoryStream::Realloc 0x4DEE40 (thiscall, new capacity, ret 4) is the only code that gives a stream a buffer of its
//   own: it builds a NEW SharedPointer with the constructor 0x4DEBE0 (its only caller: 0x4DEE87, return 0x4DEE8C), which
//   asks the allocator for the bytes at 0x4DEC15 (`call edx` = allocator vtable +8, return 0x4DEC17: the caller [BigBlock]
//   names), copies min(new, old capacity) bytes from the old buffer, and releases the old SharedPointer (inline at
//   0x4DEED3-0x4DEF11). So a stream never grows in place: every size change is allocate + copy + free.
// - Realloc is called from SetSize 0x4DF190 (exact size; return 0x4DF1C3), Write 0x4DEFF0 (capacity * factor + increment,
//   at least what the write needs; return 0x4DF061) and SetPosition 0x4DEF30 (past the end; return 0x4DEF7F).
// - The data is freed in exactly two places, both `allocator vtable +0xC (data, 0)` under the "free the data" byte when
//   the SharedPointer's refcount ends: SharedPointer::Release 0x4DEA80 (call at 0x4DEAA7) and Realloc's inline copy of
//   it (call at 0x4DEF07). The whole .text was searched for other copies of that sequence: none.
// - The hot consumer [inferred from sizes and code, to be confirmed by Stats]: the async resource loader job 0x72A150 /
//   0x727540 gives each streamed record a MemoryStream sized once with SetSize (the record's raw size, then its decoded
//   size) and reads / RefPack-decodes into it; 1024x1024 DXT5 and DXT1 textures with mips are 1,398,256 and 699,192
//   bytes + header = the 1408 KB and 704 KB classes. EA's heap gives 128 KB+ requests their own VirtualAlloc.
//
// Patches (DllMain, before the exe's entry point; each window byte-checked first, all or nothing):
// - 0x4DEC0B (12 bytes `6A 00 51 8B 4C 24 10 51 8B C8 FF D2`: push 0; push name; push size; mov ecx,allocator; call
//   [vtable+8]) becomes `51 50 53 FF 74 24 14 E8 rel32`: push name; push allocator; push ebx (the stream: Realloc's
//   `this`, untouched by the constructor); push size; call StbAlloc. Same return address 0x4DEC17, so a fallback to the
//   game's allocator is still attributed to this site by [BigBlock] / [MemDiag] AllocSites.
// - 0x4DEA99 and 0x4DEEF9 (16 bytes each, `8B 4E 04 8B ?? 8B ?? 08 8B ?? 0C 6A 00 5? FF D?`: the free call) become
//   `56 E8 rel32 EB 08 CC*8`: push esi (the SharedPointer); call StbFree; jmp over the rest.
// A block is ours when its address lies inside the arena (a range compare, nothing is read): every other pointer goes
// to the game's free exactly as before. An arena block's pointer is valid from the allocation to the free of its
// SharedPointer, as the game's own block was; nothing else in the game frees stream data (above).
//
// Arena (v2; docs/547 §11): ONE reservation of PoolMB, made once at the start (or when the pool is first switched on),
// placed at the highest address of the address space where it fits (StaPlace, from the live map), cut into 64 KB
// slices. A request of MinKB to MaxBlockMB takes the smallest free run of slices that holds it (best fit); slices that
// are not committed are committed with one call (zero pages from the OS), slices that still hold an older block's
// bytes are zero-filled over the requested part (Zero=1, default: 0x727540 accepts a short read, so the tail of a
// buffer can be read without having been written [verified, docs/547 §5]). A freed block's slices are free at once and
// stay committed; StbTick (this module's thread) decommits the ones nobody took for IdleSec, so the arena holds no RAM
// when streaming stops and its address-space cost is its reservation, never more. No run that fits, no arena, or a
// failed commit: the game's own path, exactly as without the module. Lock: one SRW lock around the tables, never held
// across a system call, a fill or the game's allocator; streams are allocated on the loader's job thread and freed on
// others, so the arena is global. Run-time switch: Enabled / Stats / Pool / Scratch / Zero / Verify are read again every
// 5 s (StbWatch); blocks handed out while a part was on still come back to the arena.
// Scratch (v2; docs/547 §12): the RefPack compressor 0x4EC0A0 allocates its two work tables per call and frees them
// before it returns; the same arena serves them through four more sites in that one function.
// Package (v2; docs/547 §13): the package writer's record commit 0x4A8910 reads a record into one buffer and compresses
// it into a second; both are freed before the commit returns. Six more sites in three functions.
// Grow (docs/547 §15): a small stream that grows is given more capacity than the game's 1.5x from a few bytes, and a
// stream's arena block grows where it is when the slices behind it are free. Timing (§16): the stubs' own time.
// Assemble (§17): the block assembler 0x6165A0's scratch buffer from the arena.
// Shared (§23): the arena as a view of a named section a 64-bit host can map.
// PreZero (§21): free slices are zeroed by the upkeep thread, so the caller's thread fills nothing. SkipClear (§21):
// no zero-fill for the sites whose consumer initialises the buffer itself (the compressor's tables, the assembler).
// Verify (debug): 1 = a freed block is filled with 0xB5 and checked when its slices are reused (a write after free
// counts as uafw). 2 = also every block is handed out filled with 0xA7 instead of zeros, and at its free the bytes that
// still hold 0xA7 are counted (unwritten): a consumer that never leaves any is proven not to depend on zeros. Limits: a
// read after free and a read before write that is later overwritten cannot be seen; with 2 an unwritten byte reads
// 0xA7, not 0.
// Apex Radiance: no Apex site lies in 0x4DE000-0x4DFFFF (docs/data/apex-sites.csv, apex-hooks.csv), so the stream sites
// do not stand down; Scratch does (the compressor sits behind the codec slot Apex's FastCacheCompression takes). A
// foreign patch is caught by the byte check at start and by StbWatch later (the module stops for the session).
#include <sddl.h>  // Shared: the section's security descriptor
#pragma comment(lib, "advapi32.lib")
static const DWORD STB_CTOR = 0x004DEBE0, STB_REL = 0x004DEA80, STB_REALLOC = 0x004DEE40;  // the three patched functions
static const DWORD STB_SETSIZE = 0x004DF190, STB_WRITE = 0x004DEFF0, STB_SETPOS = 0x004DEF30;  // Realloc's callers (frames read, no patch)
static const DWORD STB_CTOR_AT = 0x2B, STB_REL_AT = 0x19, STB_REALLOC_AT = 0xB9;  // patch offsets: 0x4DEC0B, 0x4DEA99, 0x4DEEF9
static const DWORD STB_RET_CTOR = 0x004DEE8C, STB_RET_SETSIZE = 0x004DF1C3, STB_RET_WRITE = 0x004DF061, STB_RET_SETPOS = 0x004DEF7F;
static const u8 kStbCtor[0x4B] = {  // 0x4DEBE0-0x4DEC2A: SharedPointer(size, allocator, name)
    0x8B, 0x44, 0x24, 0x08, 0x85, 0xC0, 0x56, 0x8B, 0xF1, 0xC7, 0x06, 0xD0, 0x84, 0xFB, 0x00, 0x75, 0x05, 0xE8, 0xDA, 0xAE, 0xFF, 0xFF, 0x8B, 0x4C,
    0x24, 0x10, 0x85, 0xC9, 0x89, 0x46, 0x04, 0x75, 0x05, 0xB9, 0xD4, 0x84, 0xFB, 0x00, 0x8B, 0x10, 0x8B, 0x52, 0x08, 0x6A, 0x00, 0x51, 0x8B, 0x4C,
    0x24, 0x10, 0x51, 0x8B, 0xC8, 0xFF, 0xD2, 0x89, 0x46, 0x08, 0xC7, 0x46, 0x0C, 0x00, 0x00, 0x00, 0x00, 0xC6, 0x46, 0x10, 0x01, 0x8B, 0xC6, 0x5E,
    0xC2, 0x0C, 0x00};
static const u8 kStbRel[0x37] = {  // 0x4DEA80-0x4DEAB6: SharedPointer::Release
    0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x0C, 0x83, 0xF8, 0x01, 0x7E, 0x08, 0x83, 0xC0, 0xFF, 0x89, 0x46, 0x0C, 0x5E, 0xC3, 0x80, 0x7E, 0x10, 0x00, 0x74,
    0x10, 0x8B, 0x4E, 0x04, 0x8B, 0x01, 0x8B, 0x56, 0x08, 0x8B, 0x40, 0x0C, 0x6A, 0x00, 0x52, 0xFF, 0xD0, 0x8B, 0x16, 0x8B, 0x02, 0x6A, 0x01, 0x8B,
    0xCE, 0xFF, 0xD0, 0x33, 0xC0, 0x5E, 0xC3};
static const u8 kStbRealloc[0xE2] = {  // 0x4DEE40-0x4DEF21: MemoryStream::Realloc
    0x53, 0x55, 0x8B, 0xD9, 0x8B, 0x43, 0x04, 0x85, 0xC0, 0x56, 0x57, 0x74, 0x07, 0x8B, 0x40, 0x04, 0x85, 0xC0, 0x75, 0x05, 0xE8, 0x77, 0xAC, 0xFF,
    0xFF, 0x8B, 0x6C, 0x24, 0x14, 0x85, 0xED, 0x8B, 0xF0, 0x74, 0x49, 0x8B, 0x43, 0x08, 0x85, 0xC0, 0x75, 0x05, 0xB8, 0xF8, 0x84, 0xFB, 0x00, 0x50,
    0x56, 0x6A, 0x14, 0xE8, 0x98, 0x16, 0x00, 0x00, 0x83, 0xC4, 0x0C, 0x85, 0xC0, 0x74, 0x24, 0x8B, 0x4B, 0x08, 0x51, 0x56, 0x55, 0x8B, 0xC8, 0xE8,
    0x54, 0xFD, 0xFF, 0xFF, 0x8B, 0xF8, 0x85, 0xFF, 0x74, 0x11, 0x83, 0x47, 0x0C, 0x01, 0x83, 0x7F, 0x08, 0x00, 0x75, 0x12, 0x8B, 0xCF, 0xE8, 0xDD,
    0xFB, 0xFF, 0xFF, 0x5F, 0x5E, 0x5D, 0x32, 0xC0, 0x5B, 0xC2, 0x04, 0x00, 0x33, 0xFF, 0x8B, 0x4B, 0x04, 0x85, 0xC9, 0x74, 0x5E, 0x85, 0xFF, 0x74,
    0x1A, 0x8B, 0x43, 0x14, 0x3B, 0xE8, 0x73, 0x02, 0x8B, 0xC5, 0x8B, 0x51, 0x08, 0x50, 0x8B, 0x47, 0x08, 0x52, 0x50, 0xE8, 0xD6, 0xF9, 0xA9, 0x00,
    0x83, 0xC4, 0x0C, 0x8B, 0x73, 0x04, 0x8B, 0x46, 0x0C, 0x83, 0xF8, 0x01, 0x7E, 0x15, 0x83, 0xC0, 0xFF, 0x89, 0x46, 0x0C, 0x89, 0x7B, 0x04, 0x5F,
    0x5E, 0x89, 0x6B, 0x14, 0x5D, 0xB0, 0x01, 0x5B, 0xC2, 0x04, 0x00, 0x80, 0x7E, 0x10, 0x00, 0x74, 0x10, 0x8B, 0x4E, 0x04, 0x8B, 0x11, 0x8B, 0x46,
    0x08, 0x8B, 0x52, 0x0C, 0x6A, 0x00, 0x50, 0xFF, 0xD2, 0x8B, 0x06, 0x8B, 0x10, 0x6A, 0x01, 0x8B, 0xCE, 0xFF, 0xD2, 0x89, 0x7B, 0x04, 0x5F, 0x5E,
    0x89, 0x6B, 0x14, 0x5D, 0xB0, 0x01, 0x5B, 0xC2, 0x04, 0x00};
static const u8 kStbSetSize[0x33] = {  // 0x4DF190-0x4DF1C2: push esi; push edi; ... push edi; mov ecx,esi; call Realloc
    0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x10, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x3B, 0xF9, 0xB0, 0x01, 0x74, 0x30, 0x80, 0x7E, 0x1C, 0x00, 0x74, 0x28, 0x3B,
    0xF9, 0x73, 0x10, 0x39, 0x7E, 0x18, 0x89, 0x7E, 0x10, 0x76, 0x1E, 0x89, 0x7E, 0x18, 0x5F, 0x5E, 0xC2, 0x04, 0x00, 0x57, 0x8B, 0xCE, 0xE8, 0x7D,
    0xFC, 0xFF, 0xFF};
static const u8 kStbWrite[0x71] = {  // 0x4DEFF0-0x4DF060: sub esp,8; push ebp; push esi; push ebx; push edi; ... push eax; call Realloc
    0x83, 0xEC, 0x08, 0x55, 0x8B, 0x6C, 0x24, 0x14, 0x85, 0xED, 0x56, 0x8B, 0xF1, 0x0F, 0x86, 0xA7, 0x00, 0x00, 0x00, 0x8B, 0x4E, 0x18, 0x8B, 0x46,
    0x14, 0x53, 0x57, 0x8D, 0x3C, 0x29, 0x3B, 0xF8, 0x8B, 0xDD, 0x76, 0x62, 0x80, 0x7E, 0x1C, 0x00, 0x74, 0x55, 0x85, 0xC0, 0x89, 0x44, 0x24, 0x20,
    0xDB, 0x44, 0x24, 0x20, 0x7D, 0x06, 0xD8, 0x05, 0x40, 0x89, 0xF9, 0x00, 0xD8, 0x4E, 0x20, 0x8B, 0xCE, 0xD9, 0x7C, 0x24, 0x20, 0x0F, 0xB7, 0x44,
    0x24, 0x20, 0xDA, 0x46, 0x24, 0x0D, 0x00, 0x0C, 0x00, 0x00, 0x89, 0x44, 0x24, 0x10, 0xD9, 0x6C, 0x24, 0x10, 0xDF, 0x7C, 0x24, 0x10, 0x8B, 0x44,
    0x24, 0x10, 0x3B, 0xC7, 0x0F, 0x42, 0xC7, 0xD9, 0x6C, 0x24, 0x20, 0x50, 0xE8, 0xDF, 0xFD, 0xFF, 0xFF};
static const u8 kStbSetPos[0x4F] = {  // 0x4DEF30-0x4DEF7E: push esi; push edi; ... push eax; mov ecx,esi; call Realloc
    0x8B, 0x44, 0x24, 0x08, 0x83, 0xE8, 0x00, 0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x18, 0x57, 0x8B, 0xF9, 0x74, 0x1E, 0x83, 0xE8, 0x01, 0x74, 0x0E, 0x83,
    0xE8, 0x01, 0x75, 0x1B, 0x8B, 0x46, 0x10, 0x03, 0x44, 0x24, 0x0C, 0xEB, 0x0F, 0x8B, 0x54, 0x24, 0x0C, 0x03, 0xCA, 0x89, 0x4E, 0x18, 0xEB, 0x07,
    0x8B, 0x44, 0x24, 0x0C, 0x89, 0x46, 0x18, 0x8B, 0x46, 0x18, 0x8B, 0x4E, 0x10, 0x3B, 0xC1, 0x76, 0x24, 0x80, 0x7E, 0x1C, 0x00, 0x74, 0x14, 0x50,
    0x8B, 0xCE, 0xE8, 0xC1, 0xFE, 0xFF, 0xFF};

// The RefPack compressor 0x4EC0A0 (cdecl: dst, cap, src, len, allocator, mode), 0x4EC0A0-0x4EC1FB. Its two scratch
// tables: the hash heads (0x40000 bytes; call at 0x4EC120, return 0x4EC122; 0x400 bytes of stack instead when len <=
// 0x4000) and the chain links ((window mask + 1) * 4 bytes = 0x80000 or 0x10000; call at 0x4EC13E, return 0x4EC140). Both
// are freed before the function returns (0x4EC1C3 the links, 0x4EC1D8 the heads), on the thread that allocated them.
static const DWORD STB_COMP = 0x004EC0A0;
static const DWORD STB_COMP_A1 = 0x72, STB_COMP_A2 = 0x8D, STB_COMP_F1 = 0x119, STB_COMP_F2 = 0x12E;  // 0x4EC112, 0x4EC12D, 0x4EC1B9, 0x4EC1CE
static const u8 kStbComp[0x15C] = {
    0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x10, 0x8B, 0x45, 0x14, 0x53, 0x3D, 0x00, 0x40, 0x00, 0x00, 0x0F, 0x9E, 0xC1, 0x3D, 0x00, 0x00, 0x00, 0x01, 0x56,
    0x57, 0x88, 0x4D, 0xFF, 0xC7, 0x45, 0xF4, 0xFB, 0x10, 0x00, 0x00, 0xC7, 0x45, 0xF8, 0x03, 0x00, 0x00, 0x00, 0xC7, 0x45, 0xF0, 0xFF, 0xFF, 0x01,
    0x00, 0x7C, 0x0E, 0xC7, 0x45, 0xF4, 0xFB, 0x90, 0x00, 0x00, 0xC7, 0x45, 0xF8, 0x04, 0x00, 0x00, 0x00, 0x8B, 0x5D, 0x1C, 0xF6, 0xC3, 0x01, 0x75,
    0x0E, 0x81, 0x4D, 0xF4, 0x00, 0x40, 0x00, 0x00, 0xC7, 0x45, 0xF0, 0xFF, 0x3F, 0x00, 0x00, 0x84, 0xC9, 0x8B, 0x7D, 0x18, 0x74, 0x0F, 0xB8, 0x00,
    0x04, 0x00, 0x00, 0xE8, 0xC8, 0x27, 0xA9, 0x00, 0x89, 0x65, 0x1C, 0xEB, 0x18, 0x8B, 0x07, 0x8B, 0x50, 0x08, 0x6A, 0x00, 0x68, 0x30, 0x90, 0xFB,
    0x00, 0x68, 0x00, 0x00, 0x04, 0x00, 0x8B, 0xCF, 0xFF, 0xD2, 0x89, 0x45, 0x1C, 0x8B, 0x4D, 0xF0, 0x8B, 0x07, 0x8B, 0x40, 0x08, 0x6A, 0x00, 0x8D,
    0x14, 0x8D, 0x04, 0x00, 0x00, 0x00, 0x68, 0x30, 0x90, 0xFB, 0x00, 0x52, 0x8B, 0xCF, 0xFF, 0xD0, 0x8B, 0x75, 0x08, 0x85, 0xF6, 0x89, 0x45, 0x18,
    0x74, 0x29, 0x66, 0x8B, 0x4D, 0xF4, 0x8B, 0x45, 0xF8, 0x8A, 0xF1, 0x50, 0x8A, 0xD5, 0x8B, 0x4D, 0x14, 0x51, 0x66, 0x89, 0x16, 0x8D, 0x56, 0x02,
    0x52, 0xE8, 0xDA, 0xF1, 0xFF, 0xFF, 0x8B, 0x45, 0xF8, 0x8D, 0x74, 0x30, 0x02, 0x8B, 0x45, 0x18, 0x83, 0xC4, 0x0C, 0xD1, 0xEB, 0x81, 0xE3, 0x01,
    0xFF, 0xFF, 0xFF, 0x80, 0x7D, 0xFF, 0x00, 0x53, 0x50, 0x74, 0x18, 0x8B, 0x4D, 0x1C, 0x8B, 0x55, 0xF0, 0x8B, 0x45, 0x14, 0x51, 0x8B, 0x4D, 0x10,
    0x52, 0x50, 0x51, 0x56, 0xE8, 0xB7, 0xF5, 0xFF, 0xFF, 0xEB, 0x16, 0x8B, 0x55, 0x1C, 0x8B, 0x45, 0xF0, 0x8B, 0x4D, 0x14, 0x52, 0x8B, 0x55, 0x10,
    0x50, 0x51, 0x52, 0x56, 0xE8, 0xDF, 0xF9, 0xFF, 0xFF, 0x8B, 0x4D, 0x18, 0x83, 0xC4, 0x1C, 0x8B, 0xF0, 0x8B, 0x07, 0x8B, 0x50, 0x0C, 0x6A, 0x00,
    0x51, 0x8B, 0xCF, 0xFF, 0xD2, 0x80, 0x7D, 0xFF, 0x00, 0x75, 0x20, 0x8B, 0x4D, 0x1C, 0x8B, 0x07, 0x8B, 0x50, 0x0C, 0x6A, 0x00, 0x51, 0x8B, 0xCF,
    0xFF, 0xD2, 0x8B, 0x45, 0xF8, 0x8D, 0x44, 0x06, 0x02, 0x8D, 0x65, 0xE4, 0x5F, 0x5E, 0x5B, 0x8B, 0xE5, 0x5D, 0xC3, 0x8B, 0x4D, 0xF8, 0x8D, 0x44,
    0x0E, 0x02, 0x8D, 0x65, 0xE4, 0x5F, 0x5E, 0x5B, 0x8B, 0xE5, 0x5D, 0xC3};

// The package writer's record commit 0x4A8910 (thiscall package, ret 8; one caller, 0x4A8EAE) and its two helpers,
// 0x4A82C0 (one caller: 0x4A8A55) and 0x4A7030 (one caller: 0x4A8307). The commit reads the record into a buffer of
// size + 0x10 bytes (name 0xFB26A4 "RecordData"; call at 0x4A89F5, return 0x4A89F7; kept in its frame at [esp+1Ch]) and
// has 0x4A7030 compress it into a second buffer of the codec's bound (name 0xFB257C "PackedRecord"; call at 0x4A7214,
// return 0x4A7216; handed back through an out pointer into the commit's frame, [esp+18h]). The packed buffer is freed at
// 0x4A72B0 (the codec failed), 0x4A8359 (it did not shrink enough) or 0x4A8AEF (after it was written to the file); the
// record buffer at 0x4A8AFF on every path. Both pointers live only in that one frame.
static const DWORD STB_PKG_FN[3] = {0x004A7030, 0x004A82C0, 0x004A8910};
static const u8 kStbPkg0[0x326] = {
    0x83, 0xEC, 0x20, 0x53, 0x55, 0x56, 0x8B, 0x74, 0x24, 0x38, 0x32, 0xC0, 0x33, 0xED, 0x39, 0x2E, 0x57, 0x8B, 0x7C, 0x24, 0x38, 0x8B, 0xD9, 0x0F,
    0x94, 0xC1, 0x8D, 0x57, 0xFF, 0x81, 0xFA, 0xFE, 0x59, 0x62, 0x02, 0x88, 0x44, 0x24, 0x12, 0x88, 0x4C, 0x24, 0x13, 0x0F, 0x87, 0x8D, 0x02, 0x00,
    0x00, 0x8B, 0x44, 0x24, 0x44, 0x0F, 0x57, 0xC0, 0x66, 0x89, 0x28, 0x39, 0xAB, 0x90, 0x03, 0x00, 0x00, 0x89, 0x6C, 0x24, 0x38, 0xF3, 0x0F, 0x11,
    0x44, 0x24, 0x14, 0x74, 0x2B, 0x8B, 0x8B, 0x90, 0x03, 0x00, 0x00, 0x8B, 0x11, 0x8B, 0x42, 0x78, 0xFF, 0xD0, 0x39, 0x28, 0x74, 0x1A, 0x8B, 0x8B,
    0x90, 0x03, 0x00, 0x00, 0x8B, 0x11, 0x8B, 0x52, 0x70, 0x8D, 0x44, 0x24, 0x14, 0x50, 0xFF, 0xD2, 0x3B, 0xC5, 0x89, 0x44, 0x24, 0x24, 0x75, 0x4B,
    0x8B, 0x4B, 0x10, 0x8B, 0x01, 0x8B, 0x50, 0x04, 0x55, 0x6A, 0x04, 0x55, 0x68, 0xA4, 0x25, 0xFB, 0x00, 0x6A, 0x08, 0xFF, 0xD2, 0x3B, 0xC5, 0x74,
    0x0F, 0x8B, 0x4B, 0x10, 0x51, 0x8B, 0xC8, 0xE8, 0x04, 0x4F, 0x04, 0x00, 0x8B, 0xF0, 0xEB, 0x02, 0x8B, 0xF5, 0x8B, 0x16, 0x8B, 0x44, 0x24, 0x34,
    0x8B, 0x52, 0x04, 0x6A, 0x01, 0x55, 0x55, 0x57, 0x50, 0x8B, 0xCE, 0xFF, 0xD2, 0x8B, 0x4C, 0x24, 0x44, 0x66, 0xC7, 0x01, 0xFF, 0xFF, 0xE9, 0xFF,
    0x00, 0x00, 0x00, 0x8B, 0x8B, 0x90, 0x03, 0x00, 0x00, 0x8B, 0x11, 0x8B, 0x42, 0x78, 0xFF, 0xD0, 0x8B, 0x8B, 0x90, 0x03, 0x00, 0x00, 0x8B, 0x11,
    0x89, 0x44, 0x24, 0x18, 0x8B, 0x42, 0x7C, 0xFF, 0xD0, 0x89, 0x44, 0x24, 0x28, 0x8B, 0x44, 0x24, 0x18, 0x33, 0xF6, 0x39, 0x30, 0xC7, 0x44, 0x24,
    0x20, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x84, 0xC1, 0x00, 0x00, 0x00, 0xEB, 0x03, 0x8D, 0x49, 0x00, 0x8B, 0x08, 0x8B, 0x11, 0x8B, 0x44, 0x24, 0x34,
    0x8B, 0x52, 0x04, 0x6A, 0x02, 0x6A, 0x00, 0x6A, 0x00, 0x57, 0x50, 0xFF, 0xD2, 0x8B, 0xC8, 0x8B, 0x44, 0x24, 0x24, 0x83, 0xF8, 0x01, 0x75, 0x6F,
    0xF3, 0x0F, 0x10, 0x05, 0x60, 0x09, 0x01, 0x01, 0x0F, 0x2F, 0x44, 0x24, 0x14, 0x77, 0x34, 0x8B, 0xC1, 0x85, 0xC0, 0x89, 0x44, 0x24, 0x2C, 0xDB,
    0x44, 0x24, 0x2C, 0x7D, 0x06, 0xD8, 0x05, 0x40, 0x89, 0xF9, 0x00, 0x8B, 0xD7, 0x85, 0xD2, 0x89, 0x54, 0x24, 0x2C, 0xDB, 0x44, 0x24, 0x2C, 0x7D,
    0x06, 0xD8, 0x05, 0x40, 0x89, 0xF9, 0x00, 0xDE, 0xF9, 0xD9, 0x44, 0x24, 0x14, 0xDF, 0xF1, 0xDD, 0xD8, 0x76, 0x3F, 0x8B, 0xE9, 0x85, 0xF6, 0x7C,
    0x53, 0x8B, 0x4C, 0x24, 0x18, 0x8B, 0x14, 0xB1, 0x8B, 0x44, 0x24, 0x28, 0x66, 0x8B, 0x0C, 0x70, 0x89, 0x54, 0x24, 0x38, 0x8B, 0x54, 0x24, 0x44,
    0x8B, 0x74, 0x24, 0x38, 0x89, 0x6C, 0x24, 0x1C, 0x66, 0x89, 0x0A, 0x8B, 0xC5, 0xEB, 0x33, 0x83, 0xF8, 0x02, 0x75, 0x0E, 0x85, 0xED, 0x74, 0x04,
    0x3B, 0xCD, 0x73, 0x06, 0x8B, 0xE9, 0x89, 0x74, 0x24, 0x20, 0x8B, 0x44, 0x24, 0x18, 0x83, 0xC6, 0x01, 0x83, 0x3C, 0xB0, 0x00, 0x8D, 0x04, 0xB0,
    0x0F, 0x85, 0x4A, 0xFF, 0xFF, 0xFF, 0x8B, 0x74, 0x24, 0x20, 0xEB, 0xA9, 0x8B, 0x74, 0x24, 0x38, 0x8B, 0xC6, 0x80, 0x7C, 0x24, 0x13, 0x00, 0x8B,
    0x6C, 0x24, 0x40, 0x74, 0x1F, 0x85, 0xC0, 0x74, 0x1B, 0x89, 0x45, 0x00, 0x8B, 0x4B, 0x10, 0x8B, 0x11, 0x6A, 0x00, 0x68, 0x7C, 0x25, 0xFB, 0x00,
    0x50, 0x8B, 0x42, 0x08, 0xFF, 0xD0, 0x8B, 0x4C, 0x24, 0x3C, 0x89, 0x01, 0x85, 0xF6, 0x0F, 0x84, 0x94, 0x00, 0x00, 0x00, 0x8B, 0x54, 0x24, 0x3C,
    0x8B, 0x0A, 0x85, 0xC9, 0x0F, 0x84, 0x86, 0x00, 0x00, 0x00, 0x8B, 0x43, 0x20, 0x83, 0xE8, 0x01, 0x74, 0x1A, 0x83, 0xE8, 0x02, 0x74, 0x0E, 0xB8,
    0x90, 0xD0, 0x03, 0x00, 0x3B, 0xC7, 0x1B, 0xC0, 0x83, 0xC0, 0x02, 0xEB, 0x1C, 0xB8, 0x02, 0x00, 0x00, 0x00, 0xEB, 0x15, 0x8B, 0x54, 0x24, 0x44,
    0x66, 0x81, 0x3A, 0xFF, 0xFF, 0xB8, 0x01, 0x00, 0x00, 0x00, 0x75, 0x05, 0xB8, 0x01, 0x00, 0x01, 0x00, 0x8B, 0x16, 0x8B, 0x52, 0x04, 0x50, 0x8B,
    0x45, 0x00, 0x50, 0x51, 0x8B, 0x4C, 0x24, 0x40, 0x57, 0x51, 0x8B, 0xCE, 0xFF, 0xD2, 0x83, 0xF8, 0xFF, 0x89, 0x45, 0x00, 0x74, 0x07, 0xC6, 0x44,
    0x24, 0x12, 0x01, 0xEB, 0x2B, 0x80, 0x7C, 0x24, 0x13, 0x00, 0xC7, 0x45, 0x00, 0x00, 0x00, 0x00, 0x00, 0x74, 0x1D, 0x8B, 0x7C, 0x24, 0x3C, 0x8B,
    0x07, 0x85, 0xC0, 0x74, 0x13, 0x8B, 0x4B, 0x10, 0x8B, 0x11, 0x6A, 0x00, 0x50, 0x8B, 0x42, 0x0C, 0xFF, 0xD0, 0xC7, 0x07, 0x00, 0x00, 0x00, 0x00,
    0x8B, 0x4C, 0x24, 0x44, 0x66, 0x81, 0x39, 0xFF, 0xFF, 0x75, 0x1D, 0x85, 0xF6, 0x8B, 0x5B, 0x10, 0x74, 0x16, 0x8B, 0x16, 0x8B, 0x02, 0x6A, 0x00,
    0x8B, 0xCE, 0xFF, 0xD0, 0x8B, 0x13, 0x8B, 0x42, 0x0C, 0x6A, 0x00, 0x56, 0x8B, 0xCB, 0xFF, 0xD0, 0x8A, 0x44, 0x24, 0x12, 0x5F, 0x5E, 0x5D, 0x5B,
    0x83, 0xC4, 0x20, 0xC2, 0x14, 0x00, 0x84, 0xC9, 0x8B, 0x4C, 0x24, 0x40, 0x74, 0x30, 0x89, 0x39, 0x8B, 0x4B, 0x10, 0x8B, 0x11, 0x8B, 0x42, 0x08,
    0x55, 0x68, 0x7C, 0x25, 0xFB, 0x00, 0x57, 0xFF, 0xD0, 0x8B, 0x4C, 0x24, 0x34, 0x57, 0x51, 0x50, 0x89, 0x06, 0xE8, 0x8F, 0x75, 0xAD, 0x00, 0x83,
    0xC4, 0x0C, 0x5F, 0x5E, 0x5D, 0xB0, 0x01, 0x5B, 0x83, 0xC4, 0x20, 0xC2, 0x14, 0x00, 0x39, 0x39, 0x72, 0x1F, 0x8B, 0x54, 0x24, 0x34, 0x57, 0x89,
    0x39, 0x8B, 0x06, 0x52, 0x50, 0xE8, 0x6C, 0x75, 0xAD, 0x00, 0x83, 0xC4, 0x0C, 0x5F, 0x5E, 0x5D, 0xB0, 0x01, 0x5B, 0x83, 0xC4, 0x20, 0xC2, 0x14,
    0x00, 0x5F, 0x5E, 0x89, 0x29, 0x5D, 0x5B, 0x83, 0xC4, 0x20, 0xC2, 0x14, 0x00, 0xCC};
static const u8 kStbPkg1[0xB1] = {
    0x53, 0x8B, 0x5C, 0x24, 0x14, 0x55, 0x8B, 0x6C, 0x24, 0x14, 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x18, 0x32, 0xC0, 0x83, 0xFF, 0x32, 0xC7, 0x45, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x8B, 0xF1, 0xC7, 0x03, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x82, 0x80, 0x00, 0x00, 0x00, 0x83, 0x7E, 0x20, 0x00, 0x74, 0x7A,
    0x81, 0xFF, 0x00, 0x24, 0xF4, 0x00, 0x77, 0x72, 0x8B, 0x44, 0x24, 0x24, 0x8B, 0x4C, 0x24, 0x14, 0x50, 0x53, 0x55, 0x57, 0x51, 0x8B, 0xCE, 0xE8,
    0x24, 0xED, 0xFF, 0xFF, 0x84, 0xC0, 0x74, 0x58, 0x8B, 0x13, 0xDB, 0x03, 0x85, 0xD2, 0x7D, 0x06, 0xD8, 0x05, 0x40, 0x89, 0xF9, 0x00, 0x85, 0xFF,
    0x89, 0x7C, 0x24, 0x1C, 0xDB, 0x44, 0x24, 0x1C, 0x7D, 0x06, 0xD8, 0x05, 0x40, 0x89, 0xF9, 0x00, 0xDE, 0xF9, 0xD9, 0x86, 0xE4, 0x02, 0x00, 0x00,
    0xDF, 0xF1, 0xDD, 0xD8, 0x76, 0x09, 0x5F, 0x5E, 0x5D, 0xB0, 0x01, 0x5B, 0xC2, 0x14, 0x00, 0x8B, 0x45, 0x00, 0x85, 0xC0, 0x74, 0x0D, 0x8B, 0x4E,
    0x10, 0x8B, 0x11, 0x6A, 0x00, 0x50, 0x8B, 0x42, 0x0C, 0xFF, 0xD0, 0xC7, 0x45, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC7, 0x03, 0x00, 0x00, 0x00, 0x00,
    0x32, 0xC0, 0x5F, 0x5E, 0x5D, 0x5B, 0xC2, 0x14, 0x00};
static const u8 kStbPkg2[0x2CA] = {
    0x83, 0xEC, 0x44, 0x53, 0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x14, 0x33, 0xDB, 0x32, 0xC0, 0x3B, 0xCB, 0x88, 0x44, 0x24, 0x0B, 0x0F, 0x84, 0x4F, 0x02,
    0x00, 0x00, 0xF6, 0xC1, 0x02, 0x0F, 0x84, 0x46, 0x02, 0x00, 0x00, 0x8B, 0x44, 0x24, 0x54, 0x8B, 0x08, 0x8B, 0x50, 0x04, 0x89, 0x4C, 0x24, 0x2C,
    0x8B, 0x48, 0x08, 0x89, 0x54, 0x24, 0x30, 0x8B, 0x50, 0x0C, 0x8B, 0x06, 0x89, 0x4C, 0x24, 0x34, 0x89, 0x54, 0x24, 0x38, 0x8B, 0x90, 0x9C, 0x00,
    0x00, 0x00, 0x8B, 0xCE, 0xFF, 0xD2, 0x8B, 0x8E, 0xD0, 0x02, 0x00, 0x00, 0x8D, 0x54, 0x24, 0x1C, 0x52, 0x89, 0x5C, 0x24, 0x20, 0x89, 0x5C, 0x24,
    0x24, 0x89, 0x5C, 0x24, 0x28, 0x66, 0x89, 0x5C, 0x24, 0x2C, 0x88, 0x5C, 0x24, 0x2E, 0x8B, 0x01, 0x8B, 0x40, 0x30, 0x8D, 0x54, 0x24, 0x30, 0x52,
    0xFF, 0xD0, 0x84, 0xC0, 0x74, 0x20, 0x38, 0x5C, 0x24, 0x2A, 0x8B, 0x54, 0x24, 0x20, 0x8B, 0x44, 0x24, 0x1C, 0x0F, 0x94, 0xC1, 0x51, 0x52, 0x50,
    0x8B, 0xCE, 0xE8, 0xA9, 0xDF, 0xFF, 0xFF, 0xC6, 0x86, 0xD8, 0x02, 0x00, 0x00, 0x01, 0x55, 0x8B, 0x6C, 0x24, 0x54, 0x8B, 0x55, 0x28, 0x8B, 0x42,
    0x1C, 0x83, 0xC5, 0x28, 0x57, 0x8B, 0xCD, 0xFF, 0xD0, 0x8B, 0xF8, 0x3B, 0xFB, 0x0F, 0x84, 0xCF, 0x01, 0x00, 0x00, 0x39, 0x5E, 0x20, 0x0F, 0x84,
    0x35, 0x01, 0x00, 0x00, 0x8D, 0x4F, 0xCE, 0x81, 0xF9, 0xCE, 0x23, 0xF4, 0x00, 0x0F, 0x87, 0x26, 0x01, 0x00, 0x00, 0x8B, 0x4E, 0x10, 0x8B, 0x11,
    0x8B, 0x52, 0x08, 0x53, 0x68, 0xA4, 0x26, 0xFB, 0x00, 0x8D, 0x47, 0x10, 0x50, 0xFF, 0xD2, 0x3B, 0xC3, 0x89, 0x44, 0x24, 0x1C, 0x0F, 0x84, 0x06,
    0x01, 0x00, 0x00, 0x88, 0x5C, 0x38, 0x01, 0x88, 0x1C, 0x38, 0x8B, 0x45, 0x00, 0x8B, 0x50, 0x28, 0x53, 0x53, 0x8B, 0xCD, 0x89, 0x5C, 0x24, 0x28,
    0x89, 0x5C, 0x24, 0x20, 0xFF, 0xD2, 0x84, 0xC0, 0x0F, 0x84, 0xCB, 0x00, 0x00, 0x00, 0x8B, 0x4C, 0x24, 0x1C, 0x8B, 0x45, 0x00, 0x8B, 0x50, 0x30,
    0x57, 0x51, 0x8B, 0xCD, 0xFF, 0xD2, 0x3B, 0xF8, 0x0F, 0x85, 0xB3, 0x00, 0x00, 0x00, 0x8D, 0x44, 0x24, 0x5C, 0x50, 0x8B, 0x44, 0x24, 0x20, 0x8D,
    0x4C, 0x24, 0x24, 0x51, 0x8D, 0x54, 0x24, 0x20, 0x52, 0x57, 0x50, 0x8B, 0xCE, 0xE8, 0x66, 0xF8, 0xFF, 0xFF, 0x84, 0xC0, 0x0F, 0x84, 0x8F, 0x00,
    0x00, 0x00, 0x8B, 0x6C, 0x24, 0x20, 0x53, 0x55, 0x8D, 0x4C, 0x24, 0x1C, 0x51, 0x8B, 0xCE, 0x89, 0x5C, 0x24, 0x20, 0xE8, 0x98, 0xDE, 0xFF, 0xFF,
    0x84, 0xC0, 0x74, 0x65, 0x8B, 0x54, 0x24, 0x14, 0x8B, 0x44, 0x24, 0x18, 0x55, 0x52, 0x50, 0x8B, 0xCE, 0xE8, 0xE2, 0xF3, 0xFF, 0xFF, 0x84, 0xC0,
    0x8B, 0x4C, 0x24, 0x14, 0x74, 0x40, 0x66, 0x8B, 0x54, 0x24, 0x5C, 0x66, 0x89, 0x54, 0x24, 0x50, 0x89, 0x4C, 0x24, 0x44, 0x8B, 0x8E, 0xD0, 0x02,
    0x00, 0x00, 0x8D, 0x54, 0x24, 0x44, 0x52, 0x89, 0x6C, 0x24, 0x4C, 0x89, 0x7C, 0x24, 0x50, 0xC6, 0x44, 0x24, 0x56, 0x01, 0x8B, 0x01, 0x8B, 0x40,
    0x2C, 0x8D, 0x54, 0x24, 0x38, 0x52, 0xFF, 0xD0, 0xC6, 0x86, 0xD8, 0x02, 0x00, 0x00, 0x01, 0xC6, 0x44, 0x24, 0x13, 0x01, 0xEB, 0x0B, 0x6A, 0x01,
    0x55, 0x51, 0x8B, 0xCE, 0xE8, 0x6F, 0xDE, 0xFF, 0xFF, 0x8B, 0x4E, 0x10, 0x8B, 0x11, 0x8B, 0x44, 0x24, 0x18, 0x8B, 0x52, 0x0C, 0x53, 0x50, 0xFF,
    0xD2, 0x8B, 0x4E, 0x10, 0x8B, 0x01, 0x8B, 0x54, 0x24, 0x1C, 0x8B, 0x40, 0x0C, 0x53, 0x52, 0xFF, 0xD0, 0x8A, 0x44, 0x24, 0x13, 0x3A, 0xC3, 0x75,
    0x6E, 0x53, 0x57, 0x8D, 0x4C, 0x24, 0x64, 0x51, 0x8B, 0xCE, 0x89, 0x5C, 0x24, 0x68, 0xE8, 0xF5, 0xDD, 0xFF, 0xFF, 0x84, 0xC0, 0x74, 0x54, 0x8B,
    0x4C, 0x24, 0x58, 0x81, 0x79, 0x08, 0x92, 0xA8, 0xE4, 0x12, 0x8B, 0x6C, 0x24, 0x5C, 0x75, 0x51, 0x57, 0x55, 0xE8, 0xA9, 0x51, 0x00, 0x00, 0x3A,
    0xC3, 0x88, 0x44, 0x24, 0x13, 0x74, 0x42, 0x8B, 0x8E, 0xD0, 0x02, 0x00, 0x00, 0x8D, 0x44, 0x24, 0x44, 0x50, 0x89, 0x6C, 0x24, 0x48, 0x89, 0x7C,
    0x24, 0x4C, 0x89, 0x7C, 0x24, 0x50, 0x66, 0x89, 0x5C, 0x24, 0x54, 0xC6, 0x44, 0x24, 0x56, 0x01, 0x8B, 0x11, 0x8B, 0x52, 0x2C, 0x8D, 0x44, 0x24,
    0x38, 0x50, 0xFF, 0xD2, 0xC6, 0x86, 0xD8, 0x02, 0x00, 0x00, 0x01, 0x8A, 0x44, 0x24, 0x13, 0x5F, 0x5D, 0x5E, 0x5B, 0x83, 0xC4, 0x44, 0xC2, 0x08,
    0x00, 0x6A, 0x01, 0x57, 0x55, 0x8B, 0xCE, 0xE8, 0xC4, 0xDD, 0xFF, 0xFF, 0x8A, 0x44, 0x24, 0x13, 0x5F, 0x5D, 0x5E, 0x5B, 0x83, 0xC4, 0x44, 0xC2,
    0x08, 0x00, 0x8B, 0x8E, 0xD0, 0x02, 0x00, 0x00, 0x8D, 0x54, 0x24, 0x44, 0x52, 0x89, 0x5C, 0x24, 0x48, 0x89, 0x5C, 0x24, 0x4C, 0x89, 0x5C, 0x24,
    0x50, 0x66, 0x89, 0x5C, 0x24, 0x54, 0xC6, 0x44, 0x24, 0x56, 0x01, 0x8B, 0x01, 0x8B, 0x40, 0x2C, 0x8D, 0x54, 0x24, 0x38, 0x52, 0xFF, 0xD0, 0x5F,
    0x5D, 0xC6, 0x86, 0xD8, 0x02, 0x00, 0x00, 0x01, 0x5E, 0xB0, 0x01, 0x5B, 0x83, 0xC4, 0x44, 0xC2, 0x08, 0x00};
static const u8* const kStbPkgFn[3] = {kStbPkg0, kStbPkg1, kStbPkg2};
static const DWORD kStbPkgLen[3] = {sizeof(kStbPkg0), sizeof(kStbPkg1), sizeof(kStbPkg2)};
// The six sites, the four frees first: function, offset, length, where the call's E8 sits, the new bytes (rel32 filled
// in at install). Frees: `mov ecx,[obj+10h]` (the package's allocator, kept), [the pointer loaded as before], push
// allocator, push pointer, call StbSiteFree, nop. Allocations: allocator, [size as before], push name, push allocator,
// push size, nop, call StbPkgAlloc: the call ends where the game's ended (0x4A7216 / 0x4A89F7).
struct StbSite { u8 fn; WORD at; u8 n, call; u8 b[20]; };
static const StbSite kStbPkgSite[6] = {
    {0, 0x275, 13, 5, {0x8B, 0x4B, 0x10, 0x51, 0x50, 0xE8, 0, 0, 0, 0, 0x0F, 0x1F, 0x00}},                                  // 0x4A72A5
    {1, 0x08E, 13, 5, {0x8B, 0x4E, 0x10, 0x51, 0x50, 0xE8, 0, 0, 0, 0, 0x0F, 0x1F, 0x00}},                                  // 0x4A834E
    {2, 0x1D1, 16, 9, {0x8B, 0x4E, 0x10, 0x8B, 0x44, 0x24, 0x18, 0x51, 0x50, 0xE8, 0, 0, 0, 0, 0x66, 0x90}},                // 0x4A8AE1
    {2, 0x1E1, 16, 9, {0x8B, 0x4E, 0x10, 0x8B, 0x54, 0x24, 0x1C, 0x51, 0x52, 0xE8, 0, 0, 0, 0, 0x66, 0x90}},                // 0x4A8AF1
    {0, 0x1D4, 18, 13, {0x8B, 0x4B, 0x10, 0x68, 0x7C, 0x25, 0xFB, 0x00, 0x51, 0x50, 0x0F, 0x1F, 0x00, 0xE8}},               // 0x4A7204
    {2, 0x0D3, 20, 15, {0x8B, 0x4E, 0x10, 0x8D, 0x47, 0x10, 0x68, 0xA4, 0x26, 0xFB, 0x00, 0x51, 0x50, 0x66, 0x90, 0xE8}}};  // 0x4A89E3

// The block assembler 0x6165A0 (cdecl: stream, count, descriptors; 0x6165A0-0x616AB2; ten callers). It adds up the
// sizes of `count` pieces, takes one scratch buffer for them straight from the MemoryMan heap (`[0x11CB864]->Memalign(
// size, 16, 0, 0)`, call 0x6169A8 to 0x4E6A20, return 0x6169AD), clears it, copies a 0x80-byte header and the pieces
// into it, writes it to the stream with one Write (0x616A81) and frees it (`heap->Free(p)`, call 0x616A93 to
// 0x4E5920). The pointer stays in ebx and its frame; the only exit between the allocation and the free is the NULL
// check right behind the allocation.
static const DWORD STB_ASM = 0x006165A0, STB_ASM_ALLOC = 0x408, STB_ASM_FREE = 0x4F3;  // 0x6169A8, 0x616A93
static const u8 kStbAsm[0x513] = {
    0x81, 0xEC, 0xA0, 0x00, 0x00, 0x00, 0x53, 0x56, 0x57, 0x68, 0x80, 0x00, 0x00, 0x00, 0x33, 0xF6, 0x8D, 0x44, 0x24, 0x2C, 0x56, 0x50, 0xE8, 0xFD,
    0x82, 0x96, 0x00, 0x8B, 0xBC, 0x24, 0xC4, 0x00, 0x00, 0x00, 0x8B, 0x4F, 0x04, 0x8B, 0x57, 0x08, 0x8D, 0x47, 0x08, 0x89, 0x44, 0x24, 0x1C, 0x8B,
    0x84, 0x24, 0xC0, 0x00, 0x00, 0x00, 0x89, 0x4C, 0x24, 0x44, 0x83, 0xC4, 0x0C, 0x83, 0xF8, 0x01, 0xB9, 0x20, 0x00, 0x00, 0x00, 0xC7, 0x44, 0x24,
    0x28, 0x44, 0x44, 0x53, 0x20, 0xC7, 0x44, 0x24, 0x2C, 0x7C, 0x00, 0x00, 0x00, 0xC7, 0x44, 0x24, 0x30, 0x07, 0x10, 0x00, 0x00, 0x89, 0x54, 0x24,
    0x34, 0xC7, 0x44, 0x24, 0x40, 0x01, 0x00, 0x00, 0x00, 0x89, 0x44, 0x24, 0x44, 0xC7, 0x84, 0x24, 0x94, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00,
    0x89, 0x4C, 0x24, 0x74, 0x76, 0x13, 0xC7, 0x44, 0x24, 0x30, 0x07, 0x10, 0x02, 0x00, 0xC7, 0x84, 0x24, 0x94, 0x00, 0x00, 0x00, 0x00, 0x10, 0x40,
    0x00, 0x8B, 0x57, 0x10, 0x8D, 0x42, 0xFE, 0x83, 0xF8, 0x78, 0x0F, 0x87, 0x67, 0x04, 0x00, 0x00, 0x0F, 0xB6, 0x80, 0x08, 0x6B, 0x61, 0x00, 0xFF,
    0x24, 0x85, 0xB4, 0x6A, 0x61, 0x00, 0xC7, 0x84, 0x24, 0x80, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x84, 0x00, 0x00, 0x00,
    0x00, 0x7C, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x88, 0x00, 0x00, 0x00, 0xE0, 0x03, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x8C, 0x00, 0x00, 0x00, 0x1F, 0x00,
    0x00, 0x00, 0xBB, 0x40, 0x00, 0x00, 0x00, 0xE9, 0x04, 0x02, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x80, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0xC7,
    0x84, 0x24, 0x84, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x88, 0x00, 0x00, 0x00, 0xE0, 0x07, 0x00, 0x00, 0xC7, 0x84, 0x24,
    0x8C, 0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00, 0xBB, 0x40, 0x00, 0x00, 0x00, 0xE9, 0xCE, 0x01, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x84, 0x00, 0x00,
    0x00, 0x00, 0x00, 0xFF, 0x00, 0xC7, 0x84, 0x24, 0x8C, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xBB, 0x40, 0x00, 0x00, 0x00, 0xE9, 0x9C, 0x01,
    0x00, 0x00, 0xC7, 0x84, 0x24, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xC7, 0x84, 0x24, 0x8C, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00,
    0xBB, 0x41, 0x00, 0x00, 0x00, 0xE9, 0x71, 0x01, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x80, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0xC7, 0x84, 0x24,
    0x90, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xBB, 0x02, 0x00, 0x00, 0x00, 0xE9, 0x6E, 0x01, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x80, 0x00, 0x00,
    0x00, 0x08, 0x00, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x84, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xBB, 0x00, 0x00, 0x02, 0x00, 0xE9, 0x4E, 0x01,
    0x00, 0x00, 0xC7, 0x84, 0x24, 0x80, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x84, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00,
    0xC7, 0x84, 0x24, 0x90, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xBB, 0x01, 0x00, 0x02, 0x00, 0xE9, 0x23, 0x01, 0x00, 0x00, 0xC7, 0x44, 0x24,
    0x7C, 0x44, 0x58, 0x54, 0x31, 0xBB, 0x04, 0x00, 0x00, 0x00, 0xE9, 0x11, 0x01, 0x00, 0x00, 0xC7, 0x44, 0x24, 0x7C, 0x44, 0x58, 0x54, 0x32, 0xBB,
    0x04, 0x00, 0x00, 0x00, 0xE9, 0xFF, 0x00, 0x00, 0x00, 0xC7, 0x44, 0x24, 0x7C, 0x44, 0x58, 0x54, 0x33, 0xBB, 0x04, 0x00, 0x00, 0x00, 0xE9, 0xED,
    0x00, 0x00, 0x00, 0xC7, 0x44, 0x24, 0x7C, 0x44, 0x58, 0x54, 0x34, 0xBB, 0x04, 0x00, 0x00, 0x00, 0xE9, 0xDB, 0x00, 0x00, 0x00, 0xC7, 0x44, 0x24,
    0x7C, 0x44, 0x58, 0x54, 0x35, 0xBB, 0x04, 0x00, 0x00, 0x00, 0xE9, 0xC9, 0x00, 0x00, 0x00, 0xC7, 0x44, 0x24, 0x7C, 0x24, 0x00, 0x00, 0x00, 0xBB,
    0x04, 0x00, 0x00, 0x00, 0xE9, 0xB7, 0x00, 0x00, 0x00, 0xC7, 0x44, 0x24, 0x7C, 0x70, 0x00, 0x00, 0x00, 0xBB, 0x04, 0x00, 0x00, 0x00, 0xE9, 0xA5,
    0x00, 0x00, 0x00, 0xC7, 0x44, 0x24, 0x7C, 0x71, 0x00, 0x00, 0x00, 0xBB, 0x04, 0x00, 0x00, 0x00, 0xE9, 0x93, 0x00, 0x00, 0x00, 0xC7, 0x44, 0x24,
    0x7C, 0x72, 0x00, 0x00, 0x00, 0xBB, 0x04, 0x00, 0x00, 0x00, 0xE9, 0x81, 0x00, 0x00, 0x00, 0xC7, 0x44, 0x24, 0x7C, 0x73, 0x00, 0x00, 0x00, 0xBB,
    0x04, 0x00, 0x00, 0x00, 0xEB, 0x72, 0xC7, 0x44, 0x24, 0x7C, 0x74, 0x00, 0x00, 0x00, 0xBB, 0x04, 0x00, 0x00, 0x00, 0xEB, 0x63, 0xC7, 0x84, 0x24,
    0x80, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0xBB, 0x00, 0x00, 0x08, 0x00, 0xC7, 0x84, 0x24, 0x84, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00,
    0x89, 0xB4, 0x24, 0x8C, 0x00, 0x00, 0x00, 0x89, 0xB4, 0x24, 0x90, 0x00, 0x00, 0x00, 0xEB, 0x2D, 0xBB, 0x00, 0x00, 0x08, 0x00, 0xC7, 0x84, 0x24,
    0x84, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x8C, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xC7, 0x84, 0x24, 0x90, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x89, 0x8C, 0x24, 0x80, 0x00, 0x00, 0x00, 0xC7, 0x84, 0x24, 0x88, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00,
    0x55, 0x52, 0x89, 0x9C, 0x24, 0x80, 0x00, 0x00, 0x00, 0xE8, 0x1A, 0x0F, 0x00, 0x00, 0x8B, 0x4F, 0x04, 0x83, 0xC4, 0x04, 0x8B, 0xE8, 0x51, 0x8B,
    0xCD, 0xE8, 0xCA, 0x0F, 0x00, 0x00, 0x8B, 0x57, 0x04, 0x6A, 0x04, 0x52, 0x8B, 0xCD, 0xE8, 0xDD, 0x0F, 0x00, 0x00, 0x8B, 0xF0, 0x8B, 0x47, 0x04,
    0x50, 0x8B, 0xCD, 0xE8, 0xA0, 0x0E, 0x00, 0x00, 0x8B, 0x4C, 0x24, 0x14, 0x8B, 0x11, 0x52, 0x8B, 0xCD, 0xE8, 0xA2, 0x0E, 0x00, 0x00, 0xF6, 0xC3,
    0x04, 0x74, 0x11, 0x0F, 0xAF, 0xC6, 0x81, 0x4C, 0x24, 0x34, 0x00, 0x00, 0x08, 0x00, 0x89, 0x44, 0x24, 0x40, 0xEB, 0x09, 0x83, 0x4C, 0x24, 0x34,
    0x08, 0x89, 0x74, 0x24, 0x40, 0x33, 0xF6, 0x39, 0xB4, 0x24, 0xB8, 0x00, 0x00, 0x00, 0xBF, 0x80, 0x00, 0x00, 0x00, 0x89, 0x7C, 0x24, 0x20, 0x0F,
    0x86, 0x96, 0x00, 0x00, 0x00, 0x8B, 0x5C, 0x24, 0x14, 0xB8, 0x01, 0x00, 0x00, 0x00, 0x89, 0x44, 0x24, 0x28, 0x89, 0x44, 0x24, 0x24, 0x89, 0x5C,
    0x24, 0x18, 0xEB, 0x08, 0x8D, 0x64, 0x24, 0x00, 0x8B, 0x5C, 0x24, 0x18, 0x8B, 0x84, 0x24, 0xBC, 0x00, 0x00, 0x00, 0x8B, 0x40, 0x04, 0x8B, 0xCE,
    0xD3, 0xE8, 0x89, 0x44, 0x24, 0x1C, 0x83, 0xF8, 0x01, 0x8D, 0x44, 0x24, 0x1C, 0x77, 0x04, 0x8D, 0x44, 0x24, 0x28, 0x8B, 0x10, 0x8B, 0x4C, 0x24,
    0x14, 0x8B, 0x01, 0x8B, 0xCE, 0xD3, 0xE8, 0x89, 0x44, 0x24, 0x1C, 0x83, 0xF8, 0x01, 0x8D, 0x44, 0x24, 0x1C, 0x77, 0x04, 0x8D, 0x44, 0x24, 0x24,
    0x39, 0x53, 0xFC, 0x8B, 0x38, 0x75, 0x53, 0x39, 0x3B, 0x75, 0x4F, 0x6A, 0x04, 0x52, 0x8B, 0xCD, 0xE8, 0x1B, 0x0F, 0x00, 0x00, 0x57, 0x8B, 0xCD,
    0x8B, 0xD8, 0xE8, 0xF1, 0x0D, 0x00, 0x00, 0x0F, 0xAF, 0xC3, 0x01, 0x44, 0x24, 0x20, 0x83, 0x44, 0x24, 0x18, 0x14, 0x83, 0xC6, 0x01, 0x3B, 0xB4,
    0x24, 0xB8, 0x00, 0x00, 0x00, 0x72, 0x89, 0x8B, 0x7C, 0x24, 0x20, 0x8B, 0x0D, 0x64, 0xB8, 0x1C, 0x01, 0x6A, 0x00, 0x6A, 0x00, 0x6A, 0x10, 0x57,
    0xE8, 0x73, 0x00, 0xED, 0xFF, 0x8B, 0xD8, 0x85, 0xDB, 0x89, 0x9C, 0x24, 0xAC, 0x00, 0x00, 0x00, 0x75, 0x0D, 0x5D, 0x5F, 0x5E, 0x32, 0xC0, 0x5B,
    0x81, 0xC4, 0xA0, 0x00, 0x00, 0x00, 0xC3, 0x57, 0x6A, 0x00, 0x53, 0xE8, 0xE8, 0x7E, 0x96, 0x00, 0x8B, 0x84, 0x24, 0xC4, 0x00, 0x00, 0x00, 0x83,
    0xC4, 0x0C, 0x85, 0xC0, 0xB9, 0x20, 0x00, 0x00, 0x00, 0x8D, 0x74, 0x24, 0x2C, 0x8B, 0xFB, 0x8D, 0x93, 0x80, 0x00, 0x00, 0x00, 0xF3, 0xA5, 0x89,
    0x54, 0x24, 0x18, 0x76, 0x75, 0x8B, 0x74, 0x24, 0x14, 0x89, 0x44, 0x24, 0x1C, 0x8D, 0x49, 0x00, 0x8B, 0x7E, 0xFC, 0x57, 0x8B, 0xCD, 0xE8, 0x65,
    0x0E, 0x00, 0x00, 0x6A, 0x04, 0x57, 0x8B, 0xCD, 0x8B, 0xD8, 0xE8, 0x79, 0x0E, 0x00, 0x00, 0x89, 0x44, 0x24, 0x24, 0x8B, 0x06, 0x50, 0x8B, 0xCD,
    0xE8, 0x4B, 0x0D, 0x00, 0x00, 0x85, 0xC0, 0x8B, 0x4E, 0x04, 0x8B, 0x7E, 0xF8, 0x89, 0x4C, 0x24, 0x28, 0x76, 0x26, 0x89, 0x44, 0x24, 0x14, 0x8B,
    0x54, 0x24, 0x18, 0x53, 0x57, 0x52, 0xE8, 0x63, 0x7E, 0x96, 0x00, 0x8B, 0x44, 0x24, 0x30, 0x01, 0x44, 0x24, 0x24, 0x03, 0x7C, 0x24, 0x34, 0x83,
    0xC4, 0x0C, 0x83, 0x6C, 0x24, 0x14, 0x01, 0x75, 0xDE, 0x83, 0xC6, 0x14, 0x83, 0x6C, 0x24, 0x1C, 0x01, 0x75, 0x9D, 0x8B, 0x9C, 0x24, 0xAC, 0x00,
    0x00, 0x00, 0x8B, 0x8C, 0x24, 0xB4, 0x00, 0x00, 0x00, 0x8B, 0x44, 0x24, 0x20, 0x8B, 0x11, 0x8B, 0x52, 0x38, 0x50, 0x53, 0xC6, 0x44, 0x24, 0x1B,
    0x00, 0xFF, 0xD2, 0x84, 0xC0, 0x74, 0x05, 0xC6, 0x44, 0x24, 0x13, 0x01, 0x8B, 0x0D, 0x64, 0xB8, 0x1C, 0x01, 0x53, 0xE8, 0x88, 0xEE, 0xEC, 0xFF,
    0x8A, 0x44, 0x24, 0x13, 0x5D, 0x5F, 0x5E, 0x5B, 0x81, 0xC4, 0xA0, 0x00, 0x00, 0x00, 0xC3, 0x5F, 0x5E, 0x32, 0xC0, 0x5B, 0x81, 0xC4, 0xA0, 0x00,
    0x00, 0x00, 0xC3};

enum { STB_POOL = 1, STB_STATS = 2, STB_ZERO = 4, STB_VERIFY = 8, STB_VERIFY2 = 16, STB_SCRATCH = 32, STB_PACKAGE = 64, STB_GROW = 128, STB_TIME = 256, STB_ASSEMBLE = 512, STB_PREZERO = 1024, STB_SKIPCLEAR = 2048 };  // g_stbArm bits
enum { STB_ARENA = STB_POOL | STB_SCRATCH | STB_PACKAGE | STB_ASSEMBLE };  // the parts that take blocks from the arena
enum { STBS_FREE = 0, STBS_EXT, STBS_SMALL };  // a slot of g_stbBlk: free, one of the game's own 128 KB+ blocks tracked for Stats, or a sampled small one (its freeing thread and lifetime only)
static const DWORD STB_N = 512;  // shortcut: at most 512 of the game's blocks tracked at once; more go uncounted (untracked)
static const DWORD STB_WHO_N = 32, STB_TID_N = 16, STB_SZ_N = 14;
static const u8 STB_POISON = 0xB5, STB_PAT = 0xA7;
struct StbBlk { u8* p; DWORD req, t, who, tid; u8 state; };  // t = handed out at, ms
// Every field changes under g_stbLock.
struct StbWho { DWORD at, via, how, liveN, grows, maxReq; u64 n, bytes, big, bigB, frees, quick, xfree, lifeMs, liveB, peakB, served, fresh, fallback; };
struct StbTid { DWORD tid; u64 allocs, frees; };
struct StbStats {
    u64 reqs, bytes, big, bigB, served, servedB, fresh, fallback, tooBig, frees, xfree, grows, growB, zeroUs, zeroB, unwrittenB, commitB;
    DWORD liveN, peakLiveN, decommits, dfree, vaFail, uafw, unwrittenN, lost, untracked, stranded, noArena;
    // Grow: capacity raises (headN; headSteps = the 1.5x reallocations the game would have needed to reach the same
    // capacities: an upper bound of the reallocations avoided; headTotB = bytes added in all), headroom held now / at
    // most by live buffers (Stats), grows in place and the bytes Realloc did not have to copy for them
    u64 headN, headSteps, headTotB, inPlace, inPlaceB;
    // PreZero: bytes the upkeep thread zeroed, requests served without any fill or commit on the caller's thread.
    // SkipClear: requests of a site whose consumer initialises the buffer itself, and the bytes not zero-filled for them.
    // small*: every 64th request under MinKB (Stats): how many were freed, on another thread, and their lifetimes
    u64 bgZeroB, cleanN, skipClearN, skipClearB, smallN, smallX, smallLifeMs;
    u64 resetB;                      // Shared: bytes of idle slices handed back with MEM_RESET
    DWORD resets, resetFail;
    DWORD headB, peakHeadB, headUntracked, growBlocked;
};
// Timing: QueryPerformanceCounter ticks. arena[how] = this module's code on requests the arena served (the fill
// included), arenaFree = on their frees; pass = this module's code on requests and frees it handed to the game's
// allocator (pure overhead), passGame = the game's allocator on those; copy = Realloc's grow copies; samp* = the
// sampled game path (every STB_SAMPLEth served request: allocate, touch every page, free).
struct StbTime { LONGLONG arena[8], zero[8], arenaFree, pass, passGame, copy, sampSum, since; double sampSq; u64 arenaN, passN, copyB, copyN, sampN; DWORD seq; };  // zero[how] = the part of arena[how] spent zero-filling
static const DWORD STB_SAMPLE = 64;
static StbTime g_stbTime;
struct StbHead { const u8* p; DWORD extra; };
static const DWORD STB_HEAD_N = 65536;  // shortcut: headroom of at most 49152 live buffers is followed; more count as headUntracked
static StbHead g_stbHead[STB_HEAD_N];
static DWORD g_stbHeadN;
static DWORD StbHeadIx(const u8* p) { return (((DWORD)(uintptr_t)p >> 3) * 2654435761u) >> 16; }
static StbBlk g_stbBlk[STB_N];
static DWORD g_stbHi;                 // slots [0, g_stbHi) may be in use
static StbWho g_stbWho[STB_WHO_N];      // [0] = the frame was not readable, or the rows are taken
static StbTid g_stbTid[STB_TID_N];      // 128 KB+ blocks by thread; [0] = the rows are taken
static u64 g_stbSz[STB_SZ_N];          // requests by size: < 4 KB, < 8 KB, ... < 16 MB, larger
static StbStats g_stb;
static SRWLOCK g_stbLock = SRWLOCK_INIT;
static volatile LONG g_stbArm;        // STB_* bits the stubs act on (0 = every stub passes through to the game)
static volatile LONG g_stbDetached;   // process exit: no lock is taken any more
static int g_stbInstalled;            // the three stream sites are patched
static bool g_stbScratchIn, g_stbPkgIn, g_stbGrowIn, g_stbAsmIn;  // the four compressor / six package writer / two grow / two assembler sites are patched
static bool g_stbFrames, g_stbDead;    // Realloc's callers matched (the stream and its sizer can be read from the stack); stopped for the session
static DWORD g_stbMin = 128u << 10, g_stbMaxBlock = 8u << 20, g_stbBudget = 48u << 20, g_stbIdleMs = 5000, g_stbMinFreeMB = 512;
static wchar_t g_stbIni[MAX_PATH];
struct StbPatch { u8* at; u8 mine[20]; DWORD n; };
static StbPatch g_stbPatch[17];  // [0..2] the stream sites, [3..6] the compressor's, [7..12] the package writer's, [13..14] the grow sites, [15..16] the assembler's

static LONGLONG g_stbQpf;
static LONGLONG StbQpc() {
    LARGE_INTEGER c;
    if (!g_stbQpf) { QueryPerformanceFrequency(&c); g_stbQpf = c.QuadPart ? c.QuadPart : 1; }
    QueryPerformanceCounter(&c);
    return c.QuadPart;
}
static DWORD StbNowMs() { return (DWORD)(StbQpc() * 1000 / g_stbQpf); }
static DWORD (*g_stbNow)() = StbNowMs;  // selftest: its own clock

// --- the arena ---
// One reservation, made once, cut into 64 KB slices (the unit the OS reserves in). The design is mimalloc's arena
// (microsoft/mimalloc src/arena: one reserved range, fixed slices, a free map, commit state per slice, a freed range
// reusable at once and decommitted later only if it stayed free; MIT; no code taken), with the tables outside the arena
// as the New Vegas Heap Replacer's large-block heap keeps them (ideas only) and Chromium PartitionAlloc's top-down
// placement for a transient pool. A block is n whole slices; the slice states say everything, so nothing is ever read
// from or written to the arena's own memory to manage it, and a free slice may be decommitted.
// Slice state bits: USED = part of a block handed out (or in transit at its free), HEAD = first slice of a block the
// game holds, COM = committed, POISON = filled with 0xB5 at its free (Verify), PURGE = being decommitted by StbTick.
// A slice with neither USED nor PURGE is free; a free slice with COM still holds its last user's bytes.
enum { STA_USED = 1, STA_COM = 2, STA_POISON = 4, STA_HEAD = 8, STA_PURGE = 16, STA_ZERO = 32, STA_RESET = 64 };  // ZERO (PreZero): free, committed and known to hold zeros. RESET (Shared): free, committed, idle and handed back to the OS with MEM_RESET (contents undefined)
static const DWORD STA_MAX = 4096;      // slices: 256 MB, the most PoolMB allows
static const DWORD STA_BLK_MAX = 1024;  // slices in one block: 64 MB, the most MaxBlockMB allows
struct StaBlk { DWORD req, t, who, tid, n; u8 pat, keep; };  // of the block whose first slice this is: n slices; pat = handed out as the Verify 2 pattern; keep = grown in place, Realloc's free of the old SharedPointer is still to come
struct StaHole { DWORD base, size; };
struct Sta {
    u8* base;                           // set once, before anything is armed; never moves, never released
    DWORD n, freeN, comN, useN, peakUseN, peakComN;  // slices: all, free, committed, in blocks; peaks
    StaHole hole, big;                  // the free range it was placed in and the largest free range, both as found before the reservation
    DWORD fits, how;                    // free ranges that could hold it; how: 1 = at the chosen address, 2 = the OS's top-down choice
    HANDLE sec;                         // Shared: the section the arena is a view of (kept open for the session); NULL = private memory
};
static Sta g_sta;                       // every field but base / n changes under g_stbLock
static u8 g_staSt[STA_MAX];
static DWORD g_staT[STA_MAX];           // when the slice was last freed (ms): its idle clock
static StaBlk g_staBlk[STA_MAX];

// Where a reservation of `size` bytes goes, given the free ranges of the address space (any order): the highest 64 KB
// boundary at which it fits. 0 = nowhere. *in = the range taken, *big = the largest range, *fits = how many could hold it.
// Why the top: the game's memory grows from low addresses into the one large free range (its lower edge rose by 500 MB
// in a 12 minute run while its upper edge stayed where it was), so a block at the upper edge is never between two
// ranges that could later join, and the cost to the largest range is the reservation's own size, or nothing when a
// smaller range above it fits.
static DWORD StaPlace(const StaHole* h, DWORD n, DWORD size, StaHole* in, StaHole* big, DWORD* fits) {
    DWORD at = 0;
    *in = StaHole{}, *big = StaHole{}, *fits = 0;
    for (DWORD i = 0; i < n; i++) {
        if (h[i].size > big->size) *big = h[i];
        const u64 end = (u64)h[i].base + h[i].size;
        const DWORD lo = (h[i].base + 0xFFFF) & ~0xFFFFu;
        if (lo < h[i].base || end < (u64)lo + size) continue;
        const DWORD a = (DWORD)(end - size) & ~0xFFFFu;
        ++*fits;
        if (a > at) at = a, *in = h[i];
    }
    return at;
}
// MinFreeMB: a reservation that would cut the largest free range to less than the floor is not made.
static bool StaFloorOk(const StaHole& in, const StaHole& big, DWORD size, DWORD minFreeMB) { return in.base != big.base || (big.size - size) >> 20 >= minFreeMB; }
// The free ranges of at least `min` bytes, lowest first (at most cap; a longer list keeps the highest ones).
static DWORD StaScan(StaHole* out, DWORD cap, DWORD min) {
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    MEMORY_BASIC_INFORMATION mbi;
    DWORD k = 0;
    for (uintptr_t a = (uintptr_t)si.lpMinimumApplicationAddress; a < (uintptr_t)si.lpMaximumApplicationAddress;) {
        if (!VirtualQuery((void*)a, &mbi, sizeof(mbi)) || !mbi.RegionSize) break;
        if (mbi.State == MEM_FREE && mbi.RegionSize >= min) {
            if (k == cap) memmove(out, out + 1, (--k) * sizeof(StaHole));
            out[k++] = StaHole{(DWORD)(uintptr_t)mbi.BaseAddress, (DWORD)mbi.RegionSize};
        }
        const uintptr_t next = (uintptr_t)mbi.BaseAddress + mbi.RegionSize;
        if (next <= a) break;
        a = next;
    }
    return k;
}
// Makes the arena (once; never on a game thread: DllMain or this module's thread). False = no arena: every request takes
// the game's own path. minFreeMB: the largest free range must stay at least this long after the reservation.
// Shared (docs/547 §23): the arena as a view of a pagefile-backed section instead of private memory, so that a second
// process (the 64-bit host) can map the same bytes and work in the game's buffers without a copy.
// - The section is created here, reserved only (SEC_RESERVE): VirtualAlloc(MEM_COMMIT) on the view commits pages as it
//   does for private memory, zero-filled. Pages of a view cannot be decommitted; an idle run is handed back with
//   MEM_RESET instead (the OS may drop the pages without writing them to the page file; their contents are then
//   undefined, so such a slice is "dirty", never "known zero"). The commit charge stays until the process ends.
// - Name: Local\ts3sb-<process id>-arena. The process id keeps two game instances apart; the security descriptor is the
//   one the [WrapSvc] channel uses (the user and SYSTEM only, medium integrity label, no write-up), and a name that
//   already exists is refused (someone else made it), as the channel's host does.
// - Not executable (PAGE_READWRITE section, read / write view).
// Any failure (descriptor, name taken, no place for the view) ends in the private arena with a log line.
static bool g_stbShared;  // [StreamBuf] Shared at the start
static void StbSharedName(wchar_t* buf, size_t n, DWORD pid) { swprintf_s(buf, n, L"Local\\ts3sb-%lu-arena", pid); }
static HANDLE StaSection(DWORD size, const wchar_t* name) {
    HANDLE tok = NULL, sec = NULL;
    u8 tu[256];
    DWORD got = 0;
    wchar_t* sid = NULL;
    PSECURITY_DESCRIPTOR sd = NULL;
    const char* why = "the process token";
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok) && GetTokenInformation(tok, TokenUser, tu, sizeof(tu), &got) &&
        ConvertSidToStringSidW(((TOKEN_USER*)tu)->User.Sid, &sid)) {
        wchar_t sddl[320];
        swprintf_s(sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;%s)S:(ML;;NW;;;ME)", sid);
        why = "the security descriptor";
        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &sd, NULL)) {
            SECURITY_ATTRIBUTES sa = {sizeof(sa), sd, FALSE};
            why = "CreateFileMapping";
            sec = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE | SEC_RESERVE, 0, size, name);
            if (sec && GetLastError() == ERROR_ALREADY_EXISTS) CloseHandle(sec), sec = NULL, why = "the name exists already (not ours: refused)";
        }
    }
    const DWORD err = GetLastError();
    if (sd) LocalFree(sd);
    if (sid) LocalFree(sid);
    if (tok) CloseHandle(tok);
    if (!sec) Log("WARN stream buffer arena: Shared=1 but no section (%s, err %lu): private memory instead", why, err);
    return sec;
}
static bool StaReserve(DWORD size, DWORD minFreeMB) {
    if (g_sta.base) return true;
    static StaHole h[256];
    StaHole in = {}, big = {};
    DWORD fits = 0, how = 0;
    u8* p = NULL;
    wchar_t name[64] = L"";
    HANDLE sec = NULL;
    if (g_stbShared) StbSharedName(name, 64, GetCurrentProcessId()), sec = StaSection(size, name);
    for (int round = 0; round < 2 && !p; round++) {  // a second look if another thread took the place in between
        const DWORD at = StaPlace(h, StaScan(h, 256, size), size, &in, &big, &fits);
        if (!at) break;
        if (!StaFloorOk(in, big, size, minFreeMB)) {
            Log("FAIL stream buffer arena: not reserved: the largest free range is %lu MB and must stay at least %lu MB (MinFreeMB): the game's own path serves everything", big.size >> 20, minFreeMB);
            if (sec) CloseHandle(sec);
            return false;
        }
        p = sec ? (u8*)MapViewOfFileEx(sec, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, size, (void*)(uintptr_t)at)
                : (u8*)VirtualAlloc((void*)(uintptr_t)at, size, MEM_RESERVE, PAGE_READWRITE);
        how = 1;
    }
    if (!p && sec) {  // a view has no top-down placement of its own: without the chosen place the arena is private
        Log("WARN stream buffer arena: the shared view could not be placed (err %lu): private memory instead", GetLastError());
        CloseHandle(sec), sec = NULL;
    }
    if (!p) p = (u8*)VirtualAlloc(NULL, size, MEM_RESERVE | MEM_TOP_DOWN, PAGE_READWRITE), how = 2;
    if (!p || ((uintptr_t)p & 0xFFFF)) {
        Log("FAIL stream buffer arena: no %lu MB reservation (err %lu; largest free range %lu MB): the game's own path serves everything", size >> 20, GetLastError(), big.size >> 20);
        if (p) VirtualFree(p, 0, MEM_RELEASE);
        return false;
    }
    AcquireSRWLockExclusive(&g_stbLock);
    memset(g_staSt, 0, sizeof(g_staSt));
    g_sta = Sta{};
    g_sta.freeN = size >> 16, g_sta.hole = in, g_sta.big = big, g_sta.fits = fits, g_sta.how = how, g_sta.sec = sec;
    g_sta.base = p;
    InterlockedExchange((volatile LONG*)&g_sta.n, (LONG)(size >> 16));  // last, behind a barrier: StaOwned reads base and n without the lock
    ReleaseSRWLockExclusive(&g_stbLock);
    const bool inBig = how == 1 && in.base == big.base;
    Log("streambuf: arena reserved: 0x%08lX-0x%08lX, %lu MB, one reservation, nothing committed; %s free range 0x%08lX + %lu MB (rule: the highest address "
        "that fits; %lu free ranges could hold it); largest free range before: 0x%08lX + %lu MB, after: %lu MB%s",
        (DWORD)(uintptr_t)p, (DWORD)(uintptr_t)p + size, size >> 20, how == 1 ? "at the top of the" : "placed top-down by the OS, found", in.base, in.size >> 20, fits,
        big.base, big.size >> 20, (inBig ? big.size - size : big.size) >> 20, how != 1 ? " (not checked again)" : inBig ? "" : " (untouched: a smaller range above it fits)");
    if (sec) Log("streambuf: arena is SHARED memory: a view of the section %ls (pagefile-backed, reserved, read / write, this user and SYSTEM only); "
                 "idle slices are reset, not decommitted; a 64-bit process of this user can map the same bytes", name);
    return true;
}
static bool StaOwned(const void* p) { return (DWORD)((const u8*)p - g_sta.base) < g_sta.n << 16; }  // a range compare: no memory is read
// Lock held. The first slice of the smallest free run of at least n slices (the lowest such run), or -1. Best fit: of
// first fit, best fit and a two-ended layout it left the fewest requests unserved on the measured size mix (docs/547 §11).
// clean (PreZero): among runs of that same smallest length, the one whose first n slices need the least work on the
// caller's thread (a zeroed slice costs nothing, a dirty one a fill, an uncommitted one a commit and its page faults);
// the length rule itself is untouched, so the layout stays best fit.
static DWORD StaWork(DWORD s, DWORD n) {
    DWORD w = 0;
    for (DWORD i = 0; i < n; i++) w += (g_staSt[s + i] & STA_ZERO) ? 0 : (g_staSt[s + i] & STA_COM) ? 1 : 4;
    return w;
}
static int StaCarve(DWORD n, bool clean) {
    int best = -1;
    DWORD bestLen = ~0u, bestWork = 0;
    for (DWORD i = 0; i < g_sta.n;) {
        if (g_staSt[i] & (STA_USED | STA_PURGE)) { i++; continue; }
        DWORD j = i + 1;
        while (j < g_sta.n && !(g_staSt[j] & (STA_USED | STA_PURGE))) j++;
        if (j - i >= n && j - i < bestLen) best = (int)i, bestLen = j - i, bestWork = clean ? StaWork(i, n) : 0;
        else if (clean && j - i == bestLen && bestWork) {
            const DWORD w = StaWork(i, n);
            if (w < bestWork) best = (int)i, bestWork = w;
        }
        i = j;
    }
    return best;
}
// Lock held. Slices [s, s+n) are free again, each with the given state bits (COM, POISON) and its idle clock started.
static void StaFreeRun(DWORD s, DWORD n, u8 st, DWORD now) {
    for (DWORD i = 0; i < n; i++) g_staSt[s + i] = st, g_staT[s + i] = now;
    g_sta.freeN += n, g_sta.useN -= n;
}

// --- size policy ---
static DWORD StbCap(DWORD n) { return (n + 0xFFFF) & ~0xFFFFu; }  // a block: the request rounded up to whole slices
static bool StbPoolable(DWORD n) { return n >= g_stbMin && n <= g_stbMaxBlock && n <= STA_BLK_MAX << 16 && StbCap(n) >> 16 <= g_sta.n; }
static DWORD StbSzIx(DWORD n) {
    DWORD i = 0;
    for (DWORD t = 4096; i < STB_SZ_N - 1 && n >= t; t <<= 1) i++;
    return i;
}

// The game's allocator (EA ICoreAllocator: vtable +8 = Alloc(size, name, flags), +0xC = Free(ptr, size); thiscall).
typedef void* (__fastcall* StbGameAllocFn)(void* self, void* edx, DWORD size, const char* name, DWORD flags);
typedef void (__fastcall* StbGameFreeFn)(void* self, void* edx, void* p, DWORD size);
static void* StbGameAlloc(void* a, DWORD n, const char* name) { return ((StbGameAllocFn)(*(void***)a)[2])(a, NULL, n, name, 0); }
static void StbGameFree(void* a, void* p) { ((StbGameFreeFn)(*(void***)a)[3])(a, NULL, p, 0); }

// Who sized the stream: a = the address of StbAlloc's first argument. Above its four arguments the stack holds, by the
// byte-checked code: the constructor's saved esi and return 0x4DEE8C (a[4], a[5]), its three arguments, Realloc's four
// saved registers (a[9..12]), Realloc's return a[13] and argument a[14], then the caller's frame: SetSize and SetPosition
// two saved registers and the return (a[17]); Write four saved registers, 8 bytes of locals and the return (a[21]).
// how: 1 SetSize, 2 Write, 3 SetPosition, 0 = not read. via = the next return address into the exe above it.
static DWORD StbCreator(const DWORD* a, const DWORD* end, DWORD* via, DWORD* how) {
    *via = 0, *how = 0;
    if (!g_stbFrames || a + 24 >= end || a[5] != STB_RET_CTOR) return 0;
    int i;
    if (a[13] == STB_RET_SETSIZE) i = 17, *how = 1;
    else if (a[13] == STB_RET_WRITE) i = 21, *how = 2;
    else if (a[13] == STB_RET_SETPOS) i = 17, *how = 3;
    else return 0;
    const DWORD who = a[i];
    for (int j = i + 1; j < i + 64 && a + j < end; j++) {
        const DWORD v = a[j];
        if (v >= g_asTextLo + 8 && v < g_asTextHi && v != who && AsCallBefore((const u8*)(uintptr_t)v)) { *via = v; break; }
    }
    return who;
}
static DWORD (*g_stbWhoFn)(const DWORD*, const DWORD*, DWORD*, DWORD*) = StbCreator;  // selftest: its own

// Lock held from here to StbGone.
static DWORD StbWhoOf(DWORD at, DWORD via, DWORD how) {
    for (DWORD i = 1; at && i < STB_WHO_N; i++) {
        if (g_stbWho[i].at == at) return i;
        if (!g_stbWho[i].at) { g_stbWho[i].at = at, g_stbWho[i].via = via, g_stbWho[i].how = how; return i; }
    }
    return 0;
}
static StbTid& StbTidOf(DWORD tid) {
    for (DWORD i = 1; i < STB_TID_N; i++) {
        if (g_stbTid[i].tid == tid) return g_stbTid[i];
        if (!g_stbTid[i].tid) { g_stbTid[i].tid = tid; return g_stbTid[i]; }
    }
    return g_stbTid[0];
}
static int StbFreeSlot() {
    for (DWORD i = 0; i < STB_N; i++)
        if (g_stbBlk[i].state == STBS_FREE) { if (i >= g_stbHi) g_stbHi = i + 1; return (int)i; }
    return -1;
}
static int StbFind(const u8* p) {
    for (DWORD i = 0; i < g_stbHi; i++)
        if (g_stbBlk[i].p == p && g_stbBlk[i].state == STBS_EXT) return (int)i;
    return -1;
}
static void StbSlotClear(int i) {
    g_stbBlk[i] = StbBlk{};
    while (g_stbHi && g_stbBlk[g_stbHi - 1].state == STBS_FREE) g_stbHi--;
}
// A 128 KB+ block of row wi is live (the arena's or the game's) / gone after its lifetime.
static void StbLive(DWORD wi, DWORD req) {
    StbWho& w = g_stbWho[wi];
    w.liveN++, w.liveB += req, g_stb.liveN++;
    if (w.liveB > w.peakB) w.peakB = w.liveB;
    if (g_stb.liveN > g_stb.peakLiveN) g_stb.peakLiveN = g_stb.liveN;
}
static void StbGone(DWORD wi, DWORD req, DWORD t, DWORD tidAlloc, DWORD now, DWORD tid) {
    StbWho& w = g_stbWho[wi < STB_WHO_N ? wi : 0];
    const DWORD life = now - t;
    w.liveN--, w.liveB -= req, g_stb.liveN--;
    w.frees++, w.lifeMs += life, w.quick += life < 1000, g_stb.frees++;
    if (tid != tidAlloc) w.xfree++, g_stb.xfree++;
    StbTidOf(tid).frees++;
}

static bool StbAllIs(const u8* p, DWORD n, u8 v) {
    const u64 w = 0x0101010101010101ull * v;
    DWORD i = 0;
    for (; i + 8 <= n; i += 8)
        if (*(const u64*)(p + i) != w) return false;
    for (; i < n; i++)
        if (p[i] != v) return false;
    return true;
}
// Verify 2: the bytes of [p, p+n) that still hold the hand-out pattern (whole aligned 8-byte groups); *first = the first one.
static DWORD StbUnwritten(const u8* p, DWORD n, DWORD* first) {
    const u64 w = 0x0101010101010101ull * STB_PAT;
    DWORD k = 0;
    *first = 0;
    for (DWORD i = 0; i + 8 <= n; i += 8)
        if (*(const u64*)(p + i) == w) { if (!k) *first = i; k += 8; }
    return k;
}

// The slices [s0, s0+n) of the block at p, taken under the lock (plan = what each was before): committed where needed
// with one call from the first to the last that needs it (a committed slice in between keeps its bytes; the new pages
// are zero), checked for poison, and the block's bytes [from, to) given their hand-out contents: the Verify 2 pattern,
// or zeros where a slice kept an older block's bytes. False = no memory to commit (nothing was filled).
struct StaPrep { bool dirty; DWORD zb, cb; LONGLONG zt; };
static bool StaPrepare(u8* p, DWORD s0, DWORD n, const u8* plan, DWORD from, DWORD to, LONG arm, StaPrep* r) {
    DWORD lo = n, hi = 0;
    *r = StaPrep{};
    for (DWORD i = 0; i < n; i++)
        if (!(plan[i] & STA_COM)) { if (lo == n) lo = i; hi = i + 1; r->cb += 0x10000; }
    if (hi && !VirtualAlloc(p + ((s0 + lo) << 16), (hi - lo) << 16, MEM_COMMIT, PAGE_READWRITE)) return false;
    for (DWORD i = 0; i < n; i++)
        if ((plan[i] & STA_POISON) && !StbAllIs(p + ((s0 + i) << 16), 0x10000, STB_POISON)) r->dirty = true;
    if (arm & STB_VERIFY)  // a slice handed out as "known zero" must be zero: a write after the background fill is found here, and undone
        for (DWORD i = 0; i < n; i++)
            if ((plan[i] & STA_ZERO) && !StbAllIs(p + ((s0 + i) << 16), 0x10000, 0)) r->dirty = true, memset(p + ((s0 + i) << 16), 0, 0x10000);
    if (arm & STB_VERIFY2) {
        if (to > from) memset(p + from, STB_PAT, to - from);
        return true;
    }
    r->zt = StbQpc();
    for (DWORD i = 0; i < n; i++) {
        DWORD a = (s0 + i) << 16, b = a + 0x10000;
        if (a < from) a = from;
        if (b > to) b = to;
        if (a < b && (plan[i] & STA_COM) && !(plan[i] & STA_ZERO) && ((arm & STB_ZERO) || (plan[i] & STA_POISON))) memset(p + a, 0, b - a), r->zb += b - a;
    }
    r->zt = StbQpc() - r->zt;
    return true;
}
static void StaPrepDone(const StaPrep& r, const u8* p, DWORD size) {
    if (r.dirty || r.zb || r.cb) {
        AcquireSRWLockExclusive(&g_stbLock);
        g_stb.uafw += r.dirty, g_stb.commitB += r.cb;
        if (r.zb) g_stb.zeroUs += (u64)(r.zt * 1000000 / g_stbQpf), g_stb.zeroB += r.zb;
        ReleaseSRWLockExclusive(&g_stbLock);
    }
    if (r.dirty) Log("streambuf: VERIFY a freed block was written to before its reuse (0x%p, %lu bytes): uafw", p, size);
}

// Timing (Timing=1): ticks of this module's own code and of the game's allocator inside the stubs, and a sampled
// measurement of what the game's own path costs for a block the arena served.
struct StbTm { LONGLONG game, shadow, zero; bool served; };  // of one request: ticks in the game's allocator, in the sample, and whether the arena served it
// Lock held. Headroom given to a stream's buffer (Grow + Stats): data pointer -> bytes above what the game asked for.
static void StbHeadPut(const u8* p, DWORD extra) {
    if (g_stbHeadN * 4 >= STB_HEAD_N * 3) { g_stb.headUntracked++; return; }
    DWORD i = StbHeadIx(p);
    while (g_stbHead[i].p) i = (i + 1) & (STB_HEAD_N - 1);
    g_stbHead[i] = StbHead{p, extra}, g_stbHeadN++;
    if ((g_stb.headB += extra) > g_stb.peakHeadB) g_stb.peakHeadB = g_stb.headB;
}
static void StbHeadGone(const u8* p) {
    if (!g_stbHeadN) return;
    DWORD i = StbHeadIx(p);
    for (; g_stbHead[i].p != p; i = (i + 1) & (STB_HEAD_N - 1))
        if (!g_stbHead[i].p) return;
    g_stb.headB -= g_stbHead[i].extra, g_stbHeadN--;
    for (DWORD j = i;;) {  // close the gap: entries behind it move up while that keeps them reachable
        j = (j + 1) & (STB_HEAD_N - 1);
        if (!g_stbHead[j].p) break;
        const DWORD k = StbHeadIx(g_stbHead[j].p);
        if (i <= j ? (i < k && k <= j) : (i < k || k <= j)) continue;
        g_stbHead[i] = g_stbHead[j], i = j;
    }
    g_stbHead[i] = StbHead{};
}

// One request of a hooked site: counted, then from the arena (pool: the site's switch is on) or from the game's
// allocator with the site's own arguments. who / via / how name the requester for the "who" lines. extra = headroom
// bytes in `size` that this module added (Grow). tm (Timing) receives the ticks that are not this module's own.
// cleared = the site's consumer initialises every byte it later reads (docs/547 §21): with SkipClear no zero-fill.
static void* StbRequest(DWORD size, void* allocator, const char* name, DWORD who, DWORD via, DWORD how, bool grow, DWORD oldCap, LONG arm, bool pool, DWORD extra, bool cleared, StbTm* tm) {
    const bool big = size >= g_stbMin, skip = cleared && (arm & STB_SKIPCLEAR) && (arm & STB_ZERO) && !(arm & STB_VERIFY2);
    const LONG fillArm = skip ? arm & ~STB_ZERO : arm;
    const DWORD now = g_stbNow(), tid = GetCurrentThreadId(), n = StbCap(size) >> 16;
    u8 plan[STA_BLK_MAX];  // what each slice of the block was before it was taken (COM, POISON)
    int s = -1;
    bool need = false, sample = false, small = false;
    DWORD wi;
    AcquireSRWLockExclusive(&g_stbLock);
    wi = StbWhoOf(who, via, how);
    if (!big && (arm & STB_STATS)) small = !(g_stb.reqs & 63);
    StbWho& w = g_stbWho[wi];
    g_stb.reqs++, g_stb.bytes += size, g_stbSz[StbSzIx(size)]++, w.n++, w.bytes += size;
    if (size > w.maxReq) w.maxReq = size;
    if (grow) g_stb.grows++, w.grows++, g_stb.growB += oldCap < size ? oldCap : size;
    if (big) g_stb.big++, g_stb.bigB += size, w.big++, w.bigB += size, StbTidOf(tid).allocs++;
    if (pool && big) {
        if (!g_sta.base) g_stb.noArena++, g_stb.fallback++, w.fallback++;
        else if (!StbPoolable(size)) g_stb.tooBig++;
        else if ((s = StaCarve(n, (arm & STB_PREZERO) != 0)) < 0) g_stb.fallback++, w.fallback++, g_stb.stranded += g_sta.freeN >= n;  // stranded: enough free slices, no run of them
        else {
            for (DWORD i = 0; i < n; i++) {
                plan[i] = g_staSt[s + i];
                if (!(plan[i] & STA_COM)) need = true, g_sta.comN++;
                g_staSt[s + i] = (u8)(STA_USED | STA_COM | (i ? 0 : STA_HEAD));
            }
            g_staBlk[s] = StaBlk{size, now, wi, tid, n, (u8)((arm & STB_VERIFY2) != 0), 0};
            g_sta.freeN -= n, g_sta.useN += n;
            if (g_sta.useN > g_sta.peakUseN) g_sta.peakUseN = g_sta.useN;
            if (g_sta.comN > g_sta.peakComN) g_sta.peakComN = g_sta.comN;
            if (need) g_stb.fresh++, w.fresh++; else g_stb.served++, g_stb.servedB += size, w.served++;
            StbLive(wi, size);
            sample = tm && !(++g_stbTime.seq % STB_SAMPLE);
        }
    }
    ReleaseSRWLockExclusive(&g_stbLock);
    if (s >= 0) {  // the slices are this thread's until the pointer is handed out: nobody else reads or writes them
        u8* const p = g_sta.base + ((DWORD)s << 16);
        StaPrep r;
        if (StaPrepare(p, 0, n, plan, 0, size, fillArm, &r)) {
            StaPrepDone(r, p, size);
            if (tm) tm->zero = r.zt;
            if (skip || (!r.zb && !r.cb && (arm & STB_ZERO) && !(arm & STB_VERIFY2))) {
                AcquireSRWLockExclusive(&g_stbLock);
                if (skip) {
                    g_stb.skipClearN++;
                    for (DWORD i = 0; i < n; i++) g_stb.skipClearB += (plan[i] & (STA_COM | STA_ZERO)) == STA_COM ? 0x10000 : 0;
                } else g_stb.cleanN++;
                ReleaseSRWLockExclusive(&g_stbLock);
            }
            if (extra && (arm & STB_STATS)) {
                AcquireSRWLockExclusive(&g_stbLock);
                StbHeadPut(p, extra);
                ReleaseSRWLockExclusive(&g_stbLock);
            }
            if (tm) tm->served = true;
            if (sample) {  // what the game's own path costs for this request: allocate, touch every page once, free
                const LONGLONG t0 = StbQpc();
                u8* const q = (u8*)StbGameAlloc(allocator, size, name);
                if (q) {
                    for (DWORD i = 0; i < size; i += 4096) q[i] = 0;
                    StbGameFree(allocator, q);
                }
                tm->shadow = StbQpc() - t0;
            }
            return p;
        }
        AcquireSRWLockExclusive(&g_stbLock);  // no memory to commit: the slices are free as they were, the game's own path answers
        g_staBlk[s] = StaBlk{};
        for (DWORD i = 0; i < n; i++) g_staSt[s + i] = (u8)(plan[i] & (STA_COM | STA_POISON)), g_sta.comN -= !(plan[i] & STA_COM);
        g_sta.freeN += n, g_sta.useN -= n;
        g_stb.fresh--, g_stbWho[wi].fresh--, g_stb.vaFail++, g_stb.fallback++, g_stbWho[wi].fallback++;
        g_stbWho[wi].liveN--, g_stbWho[wi].liveB -= size, g_stb.liveN--;
        ReleaseSRWLockExclusive(&g_stbLock);
    }
    const LONGLONG g0 = tm ? StbQpc() : 0;
    u8* const p = (u8*)StbGameAlloc(allocator, size, name);
    if (tm) tm->game = StbQpc() - g0;
    if (p && (arm & STB_STATS) && (big || extra || small)) {
        AcquireSRWLockExclusive(&g_stbLock);
        if (extra) StbHeadPut(p, extra);
        if (small) {
            const int x = StbFreeSlot();
            if (x >= 0 && x < 128) g_stbBlk[x].p = p, g_stbBlk[x].req = size, g_stbBlk[x].t = now, g_stbBlk[x].tid = tid, g_stbBlk[x].state = STBS_SMALL;  // low slots only: long-lived small streams must not crowd out the 128 KB+ blocks
            else if (x >= 0) StbSlotClear(x);
        }
        if (big) {  // the game's block, tracked for its lifetime
            const int e = StbFind(p);  // a block we never saw freed (freed by a path that is not ours: none known)
            if (e >= 0) g_stb.lost++, g_stbWho[g_stbBlk[e].who].liveN--, g_stbWho[g_stbBlk[e].who].liveB -= g_stbBlk[e].req, g_stb.liveN--, StbSlotClear(e);
            const int x = StbFreeSlot();
            if (x < 0) g_stb.untracked++;
            else {
                StbBlk& b = g_stbBlk[x];
                b.p = p, b.req = size, b.t = now, b.who = wi, b.tid = tid, b.state = STBS_EXT;
                StbLive(wi, size);
            }
        }
        ReleaseSRWLockExclusive(&g_stbLock);
    }
    return p;
}
// StbRequest with the clock around it (Timing): this module's own ticks go to the requester's family when the arena
// served the request, else to the pass-through overhead; the sample feeds the estimate of the game's path.
static void* StbTimed(DWORD size, void* allocator, const char* name, DWORD who, DWORD via, DWORD how, bool grow, DWORD oldCap, LONG arm, bool pool, DWORD extra, bool cleared) {
    // a request under MinKB with nothing to count or time: the game's call and nothing else (no lock, no clock)
    if (size < g_stbMin && !(arm & (STB_STATS | STB_TIME))) return StbGameAlloc(allocator, size, name);
    if (!(arm & STB_TIME)) return StbRequest(size, allocator, name, who, via, how, grow, oldCap, arm, pool, extra, cleared, NULL);
    StbTm tm = {};
    const LONGLONG t0 = StbQpc();
    void* const p = StbRequest(size, allocator, name, who, via, how, grow, oldCap, arm, pool, extra, cleared, &tm);
    const LONGLONG own = StbQpc() - t0 - tm.game - tm.shadow;
    AcquireSRWLockExclusive(&g_stbLock);
    StbTime& t = g_stbTime;
    if (tm.served) t.arena[how & 7] += own, t.zero[how & 7] += tm.zero, t.arenaN++; else t.pass += own, t.passGame += tm.game, t.passN++;
    if (tm.shadow) t.sampN++, t.sampSum += tm.shadow, t.sampSq += (double)tm.shadow * (double)tm.shadow;
    ReleaseSRWLockExclusive(&g_stbLock);
    return p;
}

// Grow (docs/547 §15). A stream that Write grows asks Realloc for max(capacity * 1.5, what the write needs): the game's
// own policy (factor [stream+20h], 1.5 from 0x1020A7C; increment [stream+24h], 0). It is geometric, but it starts from
// the first write's size, and serialisers write four bytes or a short piece of text at a time into an empty stream:
// capacities 4, 8, 12, 18, 27, 40, 60, 90, 135, 202, ... (ten reallocations before 256 bytes, seventeen before 4 KB).
// StbGrowCap, called in front of Write's Realloc call, raises the capacity asked for:
// - a stream that already has a buffer: four times the old capacity under 4 KB, twice from there on, and at least
//   GrowFloor bytes (64), or GrowTextFloor (4096) when the writer is the text encoder 0x4B58A0 (its Write call returns
//   to 0x4B590C; it appends documents in pieces of at most 256 bytes);
// - a stream's first buffer: at least GrowFirst bytes (0 = as the game asks: the default);
// - never less than the game asked for, and nothing from MinKB on: large buffers keep the game's 1.5x and grow in
//   place in the arena instead.
// at = Write's own return address (the writer), read from Write's byte-checked frame.
static const DWORD STB_TEXT_WRITE_RET = 0x004B590C;
static DWORD g_stbGrowFloor = 64, g_stbGrowText = 4096, g_stbGrowFirst = 0, g_stbTextAt = STB_TEXT_WRITE_RET;
static DWORD g_stbTls = TLS_OUT_OF_INDEXES;  // per thread: headroom bytes of the Realloc now running, for StbAlloc (Stats)
static DWORD __stdcall StbGrowCap(const void* stream, DWORD cap, DWORD at) {
    if (!(g_stbArm & STB_GROW) || cap >= g_stbMin) return cap;
    const DWORD old = *(const DWORD*)((const u8*)stream + 0x14);
    DWORD want;
    if (!old || !*(void* const*)((const u8*)stream + 4)) want = g_stbGrowFirst;
    else {
        const DWORD floor = at == g_stbTextAt ? g_stbGrowText : g_stbGrowFloor;
        if (old >= g_stbMin) return cap;
        want = old < 4096 ? old * 4 : old * 2;
        if (want < floor) want = floor;
    }
    if (want <= cap || want >= g_stbMin) return cap;
    DWORD steps = 0;
    for (DWORD c = cap; c < want; steps++) c += c / 2 ? c / 2 : 1;  // 1.5x reallocations the game would need to reach `want`
    AcquireSRWLockExclusive(&g_stbLock);
    g_stb.headN++, g_stb.headSteps += steps, g_stb.headTotB += want - cap;
    ReleaseSRWLockExclusive(&g_stbLock);
    if ((g_stbArm & STB_STATS) && g_stbTls != TLS_OUT_OF_INDEXES) TlsSetValue(g_stbTls, (void*)(uintptr_t)(want - cap));
    return want;
}
// In place of `call Realloc` in Write (0x4DF05C): the capacity argument goes through StbGrowCap, then Realloc runs with
// Write's own return address 0x4DF061, so the frame the caller walk reads is unchanged. On entry the stack holds the
// return address, the capacity, Write's four saved registers and its 8 bytes of locals, then Write's return address.
static void* g_stbReallocFn = (void*)(uintptr_t)STB_REALLOC;  // selftest: its own
__declspec(naked) static void StbWriteGrow() {
    __asm {
        push ecx
        push dword ptr [esp + 0x24]
        push dword ptr [esp + 0x0C]
        push ecx
        call StbGrowCap
        pop ecx
        mov [esp + 4], eax
        jmp dword ptr [g_stbReallocFn]
    }
}
// In place of Realloc's `call memcpy` (0x4DEECB, cdecl): nothing to copy when the buffer was grown in place.
static void* __cdecl StbGrowCopy(void* dst, const void* src, size_t n) {
    if (dst == src) return dst;
    if (!(g_stbArm & STB_TIME)) return memcpy(dst, src, n);
    const LONGLONG t0 = StbQpc();
    memcpy(dst, src, n);
    const LONGLONG t = StbQpc() - t0;
    AcquireSRWLockExclusive(&g_stbLock);
    g_stbTime.copy += t, g_stbTime.copyB += n, g_stbTime.copyN++;
    ReleaseSRWLockExclusive(&g_stbLock);
    return dst;
}
// A stream's arena block grows where it is: Realloc is about to build a new SharedPointer of `size` bytes, copy the
// old capacity into it and release the old SharedPointer. When that release is certain to free the old buffer now
// (reference count 1, "free the data" set, same allocator) and the slices behind the block are free, the block takes
// them and its own address is the answer: Realloc's copy finds source = destination (StbGrowCopy skips it) and its
// release of the old SharedPointer comes to StbFree from Realloc's free site with the block marked `keep`, which
// clears the mark instead of freeing. As realloc extends a chunk into the free chunk behind it (dlmalloc) and
// mimalloc's mi_expand. NULL = not possible: the ordinary path.
static u8* StbGrowInPlace(const void* stream, DWORD size, void* allocator, DWORD wi, LONG arm) {
    const u8* const sp = *(const u8* const*)((const u8*)stream + 4);
    u8* const p = *(u8* const*)(sp + 8);
    if (*(const LONG*)(sp + 0xC) != 1 || !sp[0x10] || *(void* const*)(sp + 4) != allocator || !p || !StaOwned(p) || ((uintptr_t)p & 0xFFFF) || !StbPoolable(size)) return NULL;
    const DWORD s = (DWORD)(p - g_sta.base) >> 16, n = StbCap(size) >> 16, tid = GetCurrentThreadId();
    u8 plan[STA_BLK_MAX];
    DWORD oldN = 0, oldReq = 0;
    bool ok = false;
    AcquireSRWLockExclusive(&g_stbLock);
    StaBlk& b = g_staBlk[s];
    if ((g_staSt[s] & (STA_USED | STA_HEAD)) == (STA_USED | STA_HEAD) && !b.keep && size > b.req && s + n <= g_sta.n) {
        ok = true, oldN = b.n, oldReq = b.req;
        for (DWORD i = oldN; i < n && ok; i++) ok = !(g_staSt[s + i] & (STA_USED | STA_PURGE));
        if (!ok) g_stb.growBlocked++;
    }
    if (ok) {
        for (DWORD i = oldN; i < n; i++) {
            plan[i - oldN] = g_staSt[s + i];
            if (!(plan[i - oldN] & STA_COM)) g_sta.comN++;
            g_staSt[s + i] = (u8)(STA_USED | STA_COM);
        }
        g_sta.freeN -= n - oldN, g_sta.useN += n - oldN;
        if (g_sta.useN > g_sta.peakUseN) g_sta.peakUseN = g_sta.useN;
        if (g_sta.comN > g_sta.peakComN) g_sta.peakComN = g_sta.comN;
        b.n = n, b.req = size, b.keep = 1;
        StbWho& w = g_stbWho[wi];  // counted as the request it is; the block stays the live block it was, now longer
        g_stb.reqs++, g_stb.bytes += size, g_stbSz[StbSzIx(size)]++, w.n++, w.bytes += size, g_stb.grows++, w.grows++;
        if (size > w.maxReq) w.maxReq = size;
        g_stb.big++, g_stb.bigB += size, w.big++, w.bigB += size, StbTidOf(tid).allocs++;
        g_stb.inPlace++, g_stb.inPlaceB += oldReq;
        StbWho& o = g_stbWho[b.who < STB_WHO_N ? b.who : 0];
        if ((o.liveB += size - oldReq) > o.peakB) o.peakB = o.liveB;
    }
    ReleaseSRWLockExclusive(&g_stbLock);
    if (!ok) return NULL;
    StaPrep r;
    const DWORD oldEnd = oldN << 16, tail = size < oldEnd ? size : oldEnd;
    if (StaPrepare(p, oldN, n - oldN, plan, oldReq, size, arm, &r)) {
        if (!(arm & STB_VERIFY2) && (arm & STB_ZERO) && tail > oldReq) memset(p + oldReq, 0, tail - oldReq);  // the rest of the block's old last slice
        StaPrepDone(r, p, size);
        return p;
    }
    AcquireSRWLockExclusive(&g_stbLock);  // no memory to commit: the block is as it was, the ordinary path answers
    for (DWORD i = oldN; i < n; i++) g_staSt[s + i] = (u8)(plan[i - oldN] & (STA_COM | STA_POISON)), g_sta.comN -= !(plan[i - oldN] & STA_COM);
    g_sta.freeN += n - oldN, g_sta.useN -= n - oldN;
    b.n = oldN, b.req = oldReq, b.keep = 0;
    StbWho& w = g_stbWho[wi];
    g_stb.reqs--, g_stb.bytes -= size, g_stbSz[StbSzIx(size)]--, w.n--, w.bytes -= size, g_stb.grows--, w.grows--;
    g_stb.big--, g_stb.bigB -= size, w.big--, w.bigB -= size, StbTidOf(tid).allocs--;
    g_stb.inPlace--, g_stb.inPlaceB -= oldReq, g_stb.vaFail++;
    g_stbWho[b.who < STB_WHO_N ? b.who : 0].liveB -= size - oldReq;
    ReleaseSRWLockExclusive(&g_stbLock);
    return NULL;
}
// In place of the allocator call at 0x4DEC15 (reached only from MemoryStream::Realloc). stream = Realloc's `this`.
static void* __stdcall StbAlloc(DWORD size, void* stream, void* allocator, const char* name) {
    const LONG arm = g_stbArm;
    if (!arm) return StbGameAlloc(allocator, size, name);
    DWORD who = 0, via = 0, how = 0, oldCap = 0, extra = 0;
    bool grow = false;
    if (size >= g_stbMin || (arm & STB_STATS)) {
        // the arguments as the patched code pushed them: counted from the return address, never from &size. v1 passed
        // &size; its build had a /GS cookie in this function and with it copies of the arguments among the locals, so
        // &size was the copy's address and the walk read this function's own frame (every who row at=0 in the game)
        who = g_stbWhoFn((const DWORD*)_AddressOfReturnAddress() + 1, (const DWORD*)(uintptr_t)__readfsdword(4), &via, &how);
        if (how && stream) grow = *(void* const*)((const u8*)stream + 4) != NULL, oldCap = *(const DWORD*)((const u8*)stream + 0x14);
    }
    if ((arm & (STB_GROW | STB_STATS)) == (STB_GROW | STB_STATS) && g_stbTls != TLS_OUT_OF_INDEXES && (extra = (DWORD)(uintptr_t)TlsGetValue(g_stbTls)) != 0)
        TlsSetValue(g_stbTls, NULL);
    if (grow && size >= g_stbMin && (arm & (STB_GROW | STB_POOL)) == (STB_GROW | STB_POOL)) {
        const LONGLONG t0 = (arm & STB_TIME) ? StbQpc() : 0;
        AcquireSRWLockExclusive(&g_stbLock);
        const DWORD wi = StbWhoOf(who, via, how);
        ReleaseSRWLockExclusive(&g_stbLock);
        u8* const p = StbGrowInPlace(stream, size, allocator, wi, arm);
        if (p) {
            if (arm & STB_TIME) {
                const LONGLONG t = StbQpc() - t0;
                AcquireSRWLockExclusive(&g_stbLock);
                g_stbTime.arena[how & 7] += t, g_stbTime.arenaN++;
                ReleaseSRWLockExclusive(&g_stbLock);
            }
            return p;
        }
    }
    return StbTimed(size, allocator, name, who, via, how, grow, oldCap, arm, (arm & STB_POOL) != 0, extra, false);
}

// PreZero (docs/547 §21): free slices that still hold an older block's bytes are zeroed by this module's own thread,
// so a later request finds them "known zero" and its caller fills nothing. The idea of the OS's zero-page thread, and
// of the allocators that remember which free memory is known to be zero (mimalloc's is_zero, Scudo's zeroed pages).
static HANDLE g_stbZeroEvent;        // wakes the upkeep thread when slices were freed (auto-reset)
static volatile LONG g_stbZeroAsked;  // a wake is pending: frees do not signal again
static void StbZeroWake(LONG arm) {
    if ((arm & (STB_PREZERO | STB_ZERO)) == (STB_PREZERO | STB_ZERO) && g_stbZeroEvent && !InterlockedExchange(&g_stbZeroAsked, 1)) SetEvent(g_stbZeroEvent);
}
// One run of dirty free slices (the lowest, at most 16 = 1 MB) zeroed; returns the slices done, 0 = nothing to do.
// The run is marked PURGE under the lock, so no request and no in-place growth can take a slice that is being
// filled; filled outside the lock; then marked ZERO. With Verify the freed block's poison is checked before it is
// overwritten, and StaPrepare checks the zeros again when the slice is handed out.
static DWORD StbZeroSome(LONG arm) {
    if ((arm & (STB_PREZERO | STB_ZERO)) != (STB_PREZERO | STB_ZERO) || (arm & STB_VERIFY2) || !g_sta.n) return 0;
    DWORD s = g_sta.n, e;
    bool poison = false;
    AcquireSRWLockExclusive(&g_stbLock);
    auto dirtyFree = [](DWORD i) { return (g_staSt[i] & ~STA_POISON) == STA_COM; };
    for (DWORD i = 0; i < g_sta.n; i++)
        if (dirtyFree(i)) { s = i; break; }
    for (e = s; e < g_sta.n && e < s + 16 && dirtyFree(e); e++) poison |= (g_staSt[e] & STA_POISON) != 0, g_staSt[e] = (u8)(g_staSt[e] | STA_PURGE);
    ReleaseSRWLockExclusive(&g_stbLock);
    if (s == g_sta.n) return 0;
    u8* const p = g_sta.base + (s << 16);
    bool uaf = false;
    if (poison) {
        AcquireSRWLockShared(&g_stbLock);  // which of them carry poison (the states are ours while PURGE is set; read under the lock for the others' sake)
        u8 st[16];
        for (DWORD i = s; i < e; i++) st[i - s] = g_staSt[i];
        ReleaseSRWLockShared(&g_stbLock);
        for (DWORD i = s; i < e; i++)
            if ((st[i - s] & STA_POISON) && !StbAllIs(g_sta.base + (i << 16), 0x10000, STB_POISON)) uaf = true;
    }
    memset(p, 0, (e - s) << 16);
    AcquireSRWLockExclusive(&g_stbLock);
    for (DWORD i = s; i < e; i++) g_staSt[i] = (u8)(STA_COM | STA_ZERO);
    g_stb.bgZeroB += (u64)(e - s) << 16, g_stb.uafw += uaf;
    ReleaseSRWLockExclusive(&g_stbLock);
    if (uaf) Log("streambuf: VERIFY a freed block was written to before the background fill (0x%p): uafw", p);
    return e - s;
}

// An arena block comes back.
static void StbPut(u8* p) {
    if (g_stbDetached) return;  // process exit: the memory goes with the process
    const LONG arm = g_stbArm;
    const DWORD now = g_stbNow(), tid = GetCurrentThreadId(), s = (DWORD)(p - g_sta.base) >> 16;
    StaBlk b = {};
    bool work = false;
    AcquireSRWLockExclusive(&g_stbLock);
    // not the first slice of a block the game holds: freed twice (or never ours to free). Nothing happens: an arena
    // pointer never reaches the game's free
    if (((uintptr_t)p & 0xFFFF) || (g_staSt[s] & (STA_USED | STA_HEAD)) != (STA_USED | STA_HEAD)) g_stb.dfree++;
    else {
        b = g_staBlk[s];
        StbHeadGone(p);
        StbGone(b.who, b.req, b.t, b.tid, now, tid);
        work = b.pat || (arm & STB_VERIFY);
        if (work) g_staSt[s] = (u8)(g_staSt[s] & ~STA_HEAD);  // in transit: still ours, a second free is counted
        else StaFreeRun(s, b.n, STA_COM, now);
    }
    ReleaseSRWLockExclusive(&g_stbLock);
    if (!work) { StbZeroWake(arm); return; }
    DWORD first = 0;
    const DWORD un = b.pat ? StbUnwritten(p, b.req, &first) : 0;
    if (arm & STB_VERIFY) memset(p, STB_POISON, b.n << 16);
    AcquireSRWLockExclusive(&g_stbLock);
    StaFreeRun(s, b.n, (u8)(STA_COM | ((arm & STB_VERIFY) ? STA_POISON : 0)), now);
    const DWORD nth = un ? ++g_stb.unwrittenN : 0;
    g_stb.unwrittenB += un;
    const DWORD at = g_stbWho[b.who].at;
    ReleaseSRWLockExclusive(&g_stbLock);
    StbZeroWake(arm);
    if (nth && nth <= 16)
        Log("streambuf: VERIFY unwritten bytes at the free: sizer 0x%08lX, %lu of %lu bytes still hold the hand-out pattern, first at +0x%lX", at, un, b.req, first);
}
// The game's block is freed (Stats): its lifetime, and the headroom it carried.
static void StbExtGone(const u8* p) {
    const DWORD now = g_stbNow(), tid = GetCurrentThreadId();
    AcquireSRWLockExclusive(&g_stbLock);
    StbHeadGone(p);
    const int i = StbFind(p);
    if (i >= 0) StbGone(g_stbBlk[i].who, g_stbBlk[i].req, g_stbBlk[i].t, g_stbBlk[i].tid, now, tid), StbSlotClear(i);
    else
        for (DWORD k = 0; k < g_stbHi; k++)
            if (g_stbBlk[k].p == p && g_stbBlk[k].state == STBS_SMALL) {
                g_stb.smallN++, g_stb.smallX += g_stbBlk[k].tid != tid, g_stb.smallLifeMs += now - g_stbBlk[k].t;
                StbSlotClear((int)k);
                break;
            }
    ReleaseSRWLockExclusive(&g_stbLock);
}
static void StbRelease(void* allocator, u8* p) {
    const LONG arm = g_stbArm;
    const bool ours = p && StaOwned(p);
    if (!(arm & STB_TIME)) {
        if (ours) { StbPut(p); return; }
        if (p && (arm & STB_STATS)) StbExtGone(p);
        StbGameFree(allocator, p);
        return;
    }
    const LONGLONG t0 = StbQpc();
    LONGLONG g = 0;
    if (ours) StbPut(p);
    else {
        if (p && (arm & STB_STATS)) StbExtGone(p);
        g = StbQpc();
        StbGameFree(allocator, p);
        g = StbQpc() - g;
    }
    const LONGLONG own = StbQpc() - t0 - g;
    AcquireSRWLockExclusive(&g_stbLock);
    if (ours) g_stbTime.arenaFree += own; else g_stbTime.pass += own, g_stbTime.passGame += g;
    ReleaseSRWLockExclusive(&g_stbLock);
}
// Lock taken here. The block at p was grown in place and this is Realloc's release of its old SharedPointer: the
// mark goes, the block stays.
static bool StbKeepClear(const u8* p) {
    const DWORD s = (DWORD)(p - g_sta.base) >> 16;
    bool kept = false;
    AcquireSRWLockExclusive(&g_stbLock);
    if (!((uintptr_t)p & 0xFFFF) && (g_staSt[s] & (STA_USED | STA_HEAD)) == (STA_USED | STA_HEAD) && g_staBlk[s].keep) g_staBlk[s].keep = 0, kept = true;
    ReleaseSRWLockExclusive(&g_stbLock);
    return kept;
}
// In place of the two `allocator->Free(data, 0)` calls (0x4DEAA7, 0x4DEF07). sp = the SharedPointer whose last
// reference went, with its "free the data" byte set.
static const void* g_stbReallocFreeRet;  // the return address of the call from Realloc's site (0x4DEEFF)
static void __stdcall StbFree(void* sp) {
    u8* const p = *(u8* const*)((const u8*)sp + 8);
    if (p && _ReturnAddress() == g_stbReallocFreeRet && StaOwned(p) && StbKeepClear(p)) return;
    StbRelease(*(void* const*)((const u8*)sp + 4), p);
}
// In place of the compressor's two table allocations (0x4EC120, 0x4EC13E: `allocator->Alloc(size, name 0xFB9030, 0)`)
// and its two frees (0x4EC1C3, 0x4EC1D8: `allocator->Free(p, 0)`). The requester is the call's own return address.
static const char* g_stbScratchName = (const char*)(uintptr_t)0x00FB9030;  // "Services/UTFFoundation/CompressionRefPack" (selftest: its own)
static void* __stdcall StbScratchAlloc(DWORD size, void* allocator) {
    const LONG arm = g_stbArm;
    if (!(arm & STB_SCRATCH)) return StbGameAlloc(allocator, size, g_stbScratchName);
    return StbTimed(size, allocator, g_stbScratchName, (DWORD)(uintptr_t)_ReturnAddress(), 0, 4, false, 0, arm, true, 0, true);  // both cores initialise what they read (§21)
}
// In place of an `allocator->Free(p, 0)` call of the compressor or the package writer: any pointer, ours or the game's.
static void __stdcall StbSiteFree(void* p, void* allocator) { StbRelease(allocator, (u8*)p); }
// In place of the package writer's two buffer allocations (0x4A7214, 0x4A89F5: `allocator->Alloc(size, name, 0)`).
static void* __stdcall StbPkgAlloc(DWORD size, void* allocator, const char* name) {
    const LONG arm = g_stbArm;
    if (!(arm & STB_PACKAGE)) return StbGameAlloc(allocator, size, name);
    return StbTimed(size, allocator, name, (DWORD)(uintptr_t)_ReturnAddress(), 0, 5, false, 0, arm, true, 0, false);
}
// In place of the block assembler's Memalign and Free calls on the MemoryMan heap (0x6169A8, 0x616A93; thiscall on the
// heap). StbRequest and StbRelease speak to an allocator object, so they are handed one whose Alloc is the site's own
// call (Memalign(size, 16, 0, 0) on that heap) and whose Free is the heap's Free. An arena block starts on a 64 KB
// boundary, which is aligned to 16.
typedef void* (__fastcall* StbMemalignFn)(void* heap, void* edx, DWORD size, DWORD align, DWORD a3, DWORD a4);
typedef void (__fastcall* StbHeapFreeFn)(void* heap, void* edx, void* p);
static StbMemalignFn g_stbMemalign = (StbMemalignFn)(uintptr_t)0x004E6A20;  // selftest: its own
static StbHeapFreeFn g_stbHeapFree = (StbHeapFreeFn)(uintptr_t)0x004E5920;
struct StbAsmHeap { void** vt; void* heap; };
static void* __fastcall StbAsmVtAlloc(void* self, void*, DWORD size, const char*, DWORD) { return g_stbMemalign(((StbAsmHeap*)self)->heap, NULL, size, 16, 0, 0); }
static void __fastcall StbAsmVtFree(void* self, void*, void* p, DWORD) { g_stbHeapFree(((StbAsmHeap*)self)->heap, NULL, p); }
static void* g_stbAsmVt[4] = {NULL, NULL, (void*)&StbAsmVtAlloc, (void*)&StbAsmVtFree};
static void* __fastcall StbAsmAlloc(void* heap, void*, DWORD size, DWORD align, DWORD a3, DWORD a4) {
    const LONG arm = g_stbArm;
    if (!(arm & STB_ASSEMBLE) || align != 16 || a3 || a4) return g_stbMemalign(heap, NULL, size, align, a3, a4);
    StbAsmHeap h = {g_stbAsmVt, heap};
    return StbTimed(size, &h, NULL, (DWORD)(uintptr_t)_ReturnAddress(), 0, 6, false, 0, arm, true, 0, true);  // 0x6169CB clears the whole buffer
}
static void __fastcall StbAsmFree(void* heap, void*, void* p) {
    StbAsmHeap h = {g_stbAsmVt, heap};
    StbRelease(&h, (u8*)p);
}

// Upkeep, every 500 ms on this module's thread: free slices nobody took for IdleSec are decommitted (all free ones when
// nothing uses the arena any more), one run per lock hold. The run is marked PURGE under the lock, so no request can
// take it half-way (mimalloc claims its purge ranges the same way); the system call runs outside the lock.
static void StbTick(DWORD now) {
    const bool all = !(g_stbArm & STB_ARENA);
    for (DWORD from = 0; from < g_sta.n;) {
        DWORD s = g_sta.n, e;
        AcquireSRWLockExclusive(&g_stbLock);
        auto idle = [&](DWORD i) { return (g_staSt[i] & ~(STA_POISON | STA_ZERO)) == STA_COM && (all || (g_stbIdleMs && now - g_staT[i] >= g_stbIdleMs)); };
        for (DWORD i = from; i < g_sta.n; i++)
            if (idle(i)) { s = i; break; }
        for (e = s; e < g_sta.n && idle(e); e++) g_staSt[e] = (u8)(g_staSt[e] | STA_PURGE);
        ReleaseSRWLockExclusive(&g_stbLock);
        if (s == g_sta.n) break;
        if (g_sta.sec) {  // a view's pages cannot be decommitted: reset (the OS may drop them unwritten; contents undefined afterwards)
            const bool rs = VirtualAlloc(g_sta.base + (s << 16), (e - s) << 16, MEM_RESET, PAGE_READWRITE) != NULL;
            AcquireSRWLockExclusive(&g_stbLock);
            for (DWORD i = s; i < e; i++) g_staSt[i] = (u8)(STA_COM | STA_RESET);  // also after a failed reset: not tried again, dirty either way
            if (rs) g_stb.resets++, g_stb.resetB += (u64)(e - s) << 16; else g_stb.resetFail++;
            ReleaseSRWLockExclusive(&g_stbLock);
            from = e;
            continue;
        }
        const bool ok = VirtualFree(g_sta.base + (s << 16), (e - s) << 16, MEM_DECOMMIT) != 0;
        AcquireSRWLockExclusive(&g_stbLock);
        for (DWORD i = s; i < e; i++) g_staSt[i] = ok ? 0 : (u8)(g_staSt[i] & ~STA_PURGE);  // a failed decommit (never seen): committed and free as before
        if (ok) g_sta.comN -= e - s, g_stb.decommits++;
        ReleaseSRWLockExclusive(&g_stbLock);
        from = e;
    }
}

// The switches as the ini has them now.
static int StbIniMask(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"StreamBuf", L"Enabled", 0, ini) <= 0) return 0;
    const int v = GetPrivateProfileIntW(L"StreamBuf", L"Verify", 0, ini);
    const int m = (GetPrivateProfileIntW(L"StreamBuf", L"Pool", 0, ini) > 0 ? STB_POOL : 0) | (GetPrivateProfileIntW(L"StreamBuf", L"Stats", 0, ini) > 0 ? STB_STATS : 0) |
                  (GetPrivateProfileIntW(L"StreamBuf", L"Scratch", 0, ini) > 0 ? STB_SCRATCH : 0) | (GetPrivateProfileIntW(L"StreamBuf", L"Package", 0, ini) > 0 ? STB_PACKAGE : 0) |
                  (GetPrivateProfileIntW(L"StreamBuf", L"Grow", 0, ini) > 0 ? STB_GROW : 0) | (GetPrivateProfileIntW(L"StreamBuf", L"Timing", 0, ini) > 0 ? STB_TIME : 0) |
                  (GetPrivateProfileIntW(L"StreamBuf", L"Assemble", 0, ini) > 0 ? STB_ASSEMBLE : 0);
    if (!(m & STB_ARENA)) return m;
    const int z = (GetPrivateProfileIntW(L"StreamBuf", L"PreZero", 0, ini) > 0 ? STB_PREZERO : 0) | (GetPrivateProfileIntW(L"StreamBuf", L"SkipClear", 0, ini) > 0 ? STB_SKIPCLEAR : 0);
    return m | z | (GetPrivateProfileIntW(L"StreamBuf", L"Zero", 1, ini) > 0 ? STB_ZERO : 0) | (v >= 1 ? STB_VERIFY : 0) | (v >= 2 ? STB_VERIFY2 : 0);
}
// Every 5 s: a patched site that no longer holds our bytes stops the module for the session (the stubs may still be
// reached through the other sites, so they pass through and arena blocks still come home); else the ini's switches
// apply. apex = Apex Radiance is loaded and we stand down for it: the compressor is behind the codec slot its
// FastCacheCompression takes, so Scratch goes off (the stream sites are in no Apex area).
static void StbWatch(int mask, bool apex) {
    if (g_stbDead) return;
    for (const StbPatch& p : g_stbPatch)
        if (p.at && memcmp(p.at, p.mine, p.n)) {
            g_stbDead = true;
            InterlockedExchange(&g_stbArm, 0);
            Log("streambuf: stopped for this session: the bytes at 0x%p were changed by another module (nothing armed; the arena's memory is decommitted as its blocks come back)", p.at);
            return;
        }
    if (!g_stbScratchIn || apex) mask &= ~STB_SCRATCH;
    if (!g_stbPkgIn) mask &= ~STB_PACKAGE;
    if (!g_stbGrowIn) mask &= ~STB_GROW;
    if (!g_stbAsmIn) mask &= ~STB_ASSEMBLE;
    if (!(mask & STB_ARENA)) mask &= STB_STATS | STB_GROW | STB_TIME;
    const LONG was = g_stbArm;
    if (was == mask) return;
    if ((mask & STB_TIME) && !(was & STB_TIME)) g_stbTime.since = StbQpc();
    if ((mask & STB_ARENA) && !g_sta.base) StaReserve(g_stbBudget, g_stbMinFreeMB);  // switched on after the start: the arena is made now, on this thread
    InterlockedExchange(&g_stbArm, mask);
    Log("streambuf: switched at run time by the ini: arm %ld -> %d (1 pool, 2 stats, 4 zero, 8 verify, 16 verify 2, 32 scratch, 64 package, 128 grow, 256 timing, 512 assemble, 1024 prezero, 2048 skipclear)%s", was, mask,
        apex && g_stbScratchIn ? "; Scratch off for Apex Radiance" : "");
}
static void StbDetach() { InterlockedExchange(&g_stbDetached, 1), InterlockedExchange(&g_stbArm, 0); }  // DLL_PROCESS_DETACH
// The upkeep thread: every 500 ms the idle decommit, every 5 s the watch; with PreZero it is also woken by frees and
// zeroes dirty free slices, one run of at most 1 MB at a time with a yield between runs, at below-normal priority
// while PreZero is on (so a busy loader thread is never kept waiting for a core by it; no affinity is set).
static DWORD WINAPI StbThread(void*) {
    int prio = THREAD_PRIORITY_NORMAL;
    for (DWORD watch = GetTickCount(), upkeep = watch;;) {
        if (g_stbZeroEvent) WaitForSingleObject(g_stbZeroEvent, 500); else Sleep(500);
        InterlockedExchange(&g_stbZeroAsked, 0);
        const LONG arm = g_stbArm;
        const int want = (arm & STB_PREZERO) ? THREAD_PRIORITY_BELOW_NORMAL : THREAD_PRIORITY_NORMAL;
        if (want != prio) SetThreadPriority(GetCurrentThread(), prio = want);
        while (!g_stbDetached && StbZeroSome(g_stbArm)) SwitchToThread();
        const DWORD tick = GetTickCount();
        if (tick - watch >= 5000) watch = tick, StbWatch(StbIniMask(g_stbIni), g_apexFound && g_apexStandDown);
        if (tick - upkeep >= 500 && !g_stbDetached) upkeep = tick, StbTick(g_stbNow());
    }
}

static void StbSkip(const char* what, const u8* at, DWORD game, size_t n) {
    char hex[3 * 24 + 1];
    if (Readable(at, n)) Hex(at, n < 24 ? n : 24, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
    Log("SKIP stream buffer: bytes differ in %s at 0x%08lX (found %s...): not installed", what, game, hex);
}
static bool StbWriteSite(StbPatch& p, u8* at, const char* what) {
    if (!Write(at, p.mine, p.n)) {
        Log("FAIL stream buffer: write at 0x%p (err %lu) in %s: sites written before it stay (their stubs pass through), nothing of this part is armed", at, GetLastError(), what);
        return false;
    }
    p.at = at;
    return true;
}
// Checks the three windows (and the three callers' frames, which only the sizer attribution needs), then writes the two
// free sites and last the allocation site, so no arena block exists before its free is caught.
static bool StbInstallSites(u8* ctor, u8* rel, u8* realloc, const u8* setSize, const u8* write, const u8* setPos) {
    if (!Matches(ctor, kStbCtor, sizeof(kStbCtor))) { StbSkip("the SharedPointer constructor", ctor, STB_CTOR, sizeof(kStbCtor)); return false; }
    if (!Matches(rel, kStbRel, sizeof(kStbRel))) { StbSkip("SharedPointer::Release", rel, STB_REL, sizeof(kStbRel)); return false; }
    if (!Matches(realloc, kStbRealloc, sizeof(kStbRealloc))) { StbSkip("MemoryStream::Realloc", realloc, STB_REALLOC, sizeof(kStbRealloc)); return false; }
    g_stbFrames = Matches(setSize, kStbSetSize, sizeof(kStbSetSize)) && Matches(write, kStbWrite, sizeof(kStbWrite)) && Matches(setPos, kStbSetPos, sizeof(kStbSetPos));
    if (!g_stbFrames) Log("WARN stream buffer: SetSize / Write / SetPosition bytes differ: installed, but the sizer of a stream is not read (who rows empty)");
    u8* const at[3] = {rel + STB_REL_AT, realloc + STB_REALLOC_AT, ctor + STB_CTOR_AT};
    for (int i = 0; i < 3; i++) {
        StbPatch& p = g_stbPatch[i];
        if (i < 2) {  // push esi; call StbFree; jmp +8; int3 x 8
            const LONG r = (LONG)((uintptr_t)&StbFree - ((uintptr_t)at[i] + 6));
            p.n = 16, p.mine[0] = 0x56, p.mine[1] = 0xE8, memcpy(p.mine + 2, &r, 4), p.mine[6] = 0xEB, p.mine[7] = 0x08, memset(p.mine + 8, 0xCC, 8);
        } else {  // push ecx (name); push eax (allocator); push ebx (stream); push [esp+14h] (size); call StbAlloc
            static const u8 head[7] = {0x51, 0x50, 0x53, 0xFF, 0x74, 0x24, 0x14};
            const LONG r = (LONG)((uintptr_t)&StbAlloc - ((uintptr_t)at[i] + 12));
            p.n = 12, memcpy(p.mine, head, 7), p.mine[7] = 0xE8, memcpy(p.mine + 8, &r, 4);
        }
        if (!StbWriteSite(p, at[i], "the stream code")) return false;
    }
    g_stbReallocFreeRet = at[1] + 6;
    return true;
}
// The two grow sites (Grow, and Timing for the copy's time): Realloc's `call memcpy` (0x4DEECB) and Write's `call
// Realloc` (0x4DF05C), each a 5-byte call whose target changes. After StbInstallSites: Write's head matched
// (g_stbFrames) and Realloc's window matched before its free site was written; the call bytes are checked again.
static const DWORD STB_REALLOC_COPY = 0x8B, STB_WRITE_CALL = 0x6C;
static bool StbInstallGrow(u8* write, u8* realloc) {
    if (!g_stbFrames || memcmp(realloc + STB_REALLOC_COPY, kStbRealloc + STB_REALLOC_COPY, 5) || memcmp(write + STB_WRITE_CALL, kStbWrite + STB_WRITE_CALL, 5)) {
        StbSkip("Write / Realloc (Grow only; the other sites are not affected)", write, STB_WRITE, sizeof(kStbWrite));
        return false;
    }
    if (g_stbTls == TLS_OUT_OF_INDEXES) g_stbTls = TlsAlloc();
    u8* const at[2] = {realloc + STB_REALLOC_COPY, write + STB_WRITE_CALL};
    const uintptr_t to[2] = {(uintptr_t)&StbGrowCopy, (uintptr_t)&StbWriteGrow};
    for (int i = 0; i < 2; i++) {
        StbPatch& p = g_stbPatch[13 + i];
        const LONG r = (LONG)(to[i] - ((uintptr_t)at[i] + 5));
        p.n = 5, p.mine[0] = 0xE8, memcpy(p.mine + 1, &r, 4);
        if (!StbWriteSite(p, at[i], "the stream code (Grow)")) return false;
    }
    return true;
}
// The compressor's four sites (Scratch): the whole function must match, then the two frees are written, then the two
// allocations. Each new call ends where the old one ended, so the return addresses 0x4EC122 / 0x4EC140 stay and a
// request that falls back to the game's allocator is still named by them in [BigBlock] / [MemDiag] AllocSites.
static bool StbInstallScratch(u8* comp) {
    if (!Matches(comp, kStbComp, sizeof(kStbComp))) { StbSkip("the RefPack compressor (Scratch only; the stream sites are not affected)", comp, STB_COMP, sizeof(kStbComp)); return false; }
    static const u8 nop5[5] = {0x0F, 0x1F, 0x44, 0x00, 0x00};
    static const DWORD kAt[4] = {STB_COMP_F1, STB_COMP_F2, STB_COMP_A1, STB_COMP_A2};
    for (int i = 0; i < 4; i++) {
        StbPatch& p = g_stbPatch[3 + i];
        u8* const at = comp + kAt[i];
        u8* m = p.mine;
        if (i < 2) {  // push edi (allocator); push ecx (the table); call StbSiteFree; nop
            *m++ = 0x57, *m++ = 0x51, *m++ = 0xE8;
            const LONG r = (LONG)((uintptr_t)&StbSiteFree - ((uintptr_t)at + 7));
            memcpy(m, &r, 4), m += 4, memcpy(m, nop5, 5), m += 5;
        } else {  // [lea edx,[ecx*4+4]]; push edi (allocator); push 40000h / edx (size); nop; call StbScratchAlloc
            if (i == 3) memcpy(m, kStbComp + STB_COMP_A2 + 2, 7), m += 7, *m++ = 0x57, *m++ = 0x52;
            else *m++ = 0x57, *m++ = 0x68, *m++ = 0x00, *m++ = 0x00, *m++ = 0x04, *m++ = 0x00;
            memcpy(m, nop5, 5), m += 5, *m++ = 0xE8;
            const LONG r = (LONG)((uintptr_t)&StbScratchAlloc - ((uintptr_t)at + (m - p.mine) + 4));
            memcpy(m, &r, 4), m += 4;
        }
        p.n = (DWORD)(m - p.mine);
        if (!StbWriteSite(p, at, "the RefPack compressor")) return false;
    }
    return true;
}

// The package writer's six sites (Package): the three functions must match byte for byte, then the four frees are
// written, then the two allocations.
static bool StbInstallPackage(u8* const fn[3]) {
    for (int i = 0; i < 3; i++)
        if (!Matches(fn[i], kStbPkgFn[i], kStbPkgLen[i])) { StbSkip("the package writer (Package only; the other sites are not affected)", fn[i], STB_PKG_FN[i], kStbPkgLen[i]); return false; }
    for (int i = 0; i < 6; i++) {
        const StbSite& k = kStbPkgSite[i];
        StbPatch& p = g_stbPatch[7 + i];
        u8* const at = fn[k.fn] + k.at;
        const LONG r = (LONG)((i < 4 ? (uintptr_t)&StbSiteFree : (uintptr_t)&StbPkgAlloc) - ((uintptr_t)at + k.call + 5));
        p.n = k.n, memcpy(p.mine, k.b, k.n), memcpy(p.mine + k.call + 1, &r, 4);
        if (!StbWriteSite(p, at, "the package writer")) return false;
    }
    return true;
}

// The block assembler's two sites (Assemble): the whole function must match, then the free call's target is changed,
// then the allocation call's. Both stay 5-byte calls at the same place, so the return address 0x6169AD stays.
static bool StbInstallAssemble(u8* fn) {
    if (!Matches(fn, kStbAsm, sizeof(kStbAsm))) { StbSkip("the block assembler (Assemble only; the other sites are not affected)", fn, STB_ASM, sizeof(kStbAsm)); return false; }
    u8* const at[2] = {fn + STB_ASM_FREE, fn + STB_ASM_ALLOC};
    const uintptr_t to[2] = {(uintptr_t)&StbAsmFree, (uintptr_t)&StbAsmAlloc};
    for (int i = 0; i < 2; i++) {
        StbPatch& p = g_stbPatch[15 + i];
        const LONG r = (LONG)(to[i] - ((uintptr_t)at[i] + 5));
        p.n = 5, p.mine[0] = 0xE8, memcpy(p.mine + 1, &r, 4);
        if (!StbWriteSite(p, at[i], "the block assembler")) return false;
    }
    return true;
}

// The arena as the OS sees it (memory worker only): one reservation = every region from its base to its end names the
// base as its allocation base; *com = bytes the OS has committed in it.
static bool StaOsView(DWORD* com) {
    MEMORY_BASIC_INFORMATION mbi;
    const uintptr_t lo = (uintptr_t)g_sta.base, hi = lo + ((uintptr_t)g_sta.n << 16);
    *com = 0;
    for (uintptr_t a = lo; a < hi;) {
        if (!VirtualQuery((void*)a, &mbi, sizeof(mbi)) || mbi.AllocationBase != (void*)lo || mbi.State == MEM_FREE || !mbi.RegionSize) return false;
        const uintptr_t end = (uintptr_t)mbi.BaseAddress + mbi.RegionSize;
        if (mbi.State == MEM_COMMIT) *com += (DWORD)((end < hi ? end : hi) - a);
        a = end;
    }
    return lo && VirtualQuery((void*)(hi - 1), &mbi, sizeof(mbi)) && (uintptr_t)mbi.BaseAddress + mbi.RegionSize == hi;
}
// "diag: mem" mark (memdiag.cpp MdAfterSample)
static void StbMemLine(LONG n, const char* tag) {
    if (!g_stbInstalled) return;
    static StbWho who[STB_WHO_N];  // copies: formatted after the lock (one mark at a time: the memory worker)
    static StbTid tids[STB_TID_N];
    u64 sz[STB_SZ_N];
    DWORD idleN = 0, bigRun = 0, runs = 0, zeroN = 0;
    AcquireSRWLockShared(&g_stbLock);
    const StbTime tm = g_stbTime;
    const StbStats s = g_stb;
    const Sta a = g_sta;
    memcpy(who, g_stbWho, sizeof(who)), memcpy(tids, g_stbTid, sizeof(tids)), memcpy(sz, g_stbSz, sizeof(sz));
    for (DWORD i = 0, run = 0; i < a.n; i++) {
        const bool fr = !(g_staSt[i] & (STA_USED | STA_PURGE));
        idleN += fr && (g_staSt[i] & STA_COM), zeroN += fr && (g_staSt[i] & STA_ZERO);
        run = fr ? run + 1 : 0, runs += run == 1;
        if (run > bigRun) bigRun = run;
    }
    ReleaseSRWLockShared(&g_stbLock);
    const double MB = 1.0 / (1 << 20), SL = 1.0 / 16;
    Log("streambuf: #%ld %s arm=%ld requests=%llu MB=%.1f big=%llu bigMB=%.1f served=%llu servedMB=%.1f fresh=%llu fallback=%llu tooBig=%llu "
        "frees=%llu xthreadFrees=%llu grows=%llu growCopyMB=%.1f liveN=%lu peakLiveN=%lu zeroMs=%.1f zeroMB=%.1f errors: dfree=%lu vaFail=%lu uafw=%lu "
        "unwrittenN=%lu unwrittenKB=%llu lost=%lu untracked=%lu",
        n, tag, g_stbArm, s.reqs, s.bytes * MB, s.big, s.bigB * MB, s.served, s.servedB * MB, s.fresh, s.fallback, s.tooBig, s.frees, s.xfree, s.grows,
        s.growB * MB, s.liveN, s.peakLiveN, s.zeroUs / 1000.0, s.zeroB * MB, s.dfree, s.vaFail, s.uafw, s.unwrittenN, s.unwrittenB >> 10, s.lost, s.untracked);
    if (a.base) {
        DWORD osCom = 0;
        const bool one = StaOsView(&osCom);
        Log("streambuf: arena #%ld base=0x%08lX arenaMB=%lu oneReservation=%d osCommitMB=%.1f commitMB=%.1f inUseMB=%.1f idleMB=%.1f peakInUseMB=%.1f "
            "peakCommitMB=%.1f freeMB=%.1f largestFreeRunMB=%.1f freeRuns=%lu commits=%llu commitMB_total=%.1f decommits=%lu stranded=%lu noArena=%lu "
            "zeroedIdleMB=%.1f bgZeroMB=%.1f callerZeroMB=%.1f servedClean=%llu skipClearN=%llu skipClearMB=%.1f shared=%d resets=%lu resetMB=%.1f resetFail=%lu",
            n, (DWORD)(uintptr_t)a.base, a.n >> 4, one ? 1 : 0, osCom * MB, a.comN * SL, a.useN * SL, idleN * SL, a.peakUseN * SL, a.peakComN * SL, a.freeN * SL,
            bigRun * SL, runs, s.fresh, s.commitB * MB, s.decommits, s.stranded, s.noArena, zeroN * SL, s.bgZeroB * MB, s.zeroB * MB, s.cleanN, s.skipClearN,
            s.skipClearB * MB, a.sec ? 1 : 0, s.resets, s.resetB * MB, s.resetFail);
        if (s.smallN) Log("streambuf: small #%ld sampled 1 in 64 requests under MinKB: freed=%llu onAnotherThread=%llu (%.1f%%) avgLifeMs=%.0f", n, s.smallN, s.smallX,
                          100.0 * (double)s.smallX / (double)s.smallN, (double)s.smallLifeMs / (double)s.smallN);
    } else if (g_stbArm & STB_ARENA) Log("streambuf: arena #%ld none (the reservation failed at the start: see the FAIL line) noArena=%lu", n, s.noArena);
    if (s.headN || s.inPlace || s.growBlocked || (g_stbArm & STB_GROW))
        Log("streambuf: grow #%ld headroomGrants=%llu stepsSkippedUpTo=%llu headroomMB_total=%.1f headroomHeldKB=%lu peakHeldKB=%lu headUntracked=%lu inPlace=%llu "
            "notCopiedMB=%.1f inPlaceBlocked=%lu growCopyMB=%.1f (of %llu grows)",
            n, s.headN, s.headSteps, s.headTotB * MB, s.headB >> 10, s.peakHeadB >> 10, s.headUntracked, s.inPlace, s.inPlaceB * MB, s.growBlocked, s.growB * MB, s.grows);
    if (tm.since) {
        const double ms = 1000.0 / (double)g_stbQpf, secs = (double)(StbQpc() - tm.since) / (double)g_stbQpf;
        LONGLONG arena = tm.arenaFree;
        for (LONGLONG v : tm.arena) arena += v;
        // the game's path for the served requests: the sample mean times their number; 2 sigma from the samples' spread
        const u64 servedN = s.served + s.fresh;
        const double mean = tm.sampN ? (double)tm.sampSum / (double)tm.sampN : 0.0;
        const double var = tm.sampN > 1 ? (tm.sampSq - mean * mean * (double)tm.sampN) / (double)(tm.sampN - 1) : 0.0;
        const double est = mean * (double)servedN * ms, err = tm.sampN ? 2.0 * (double)servedN * sqrt(var > 0 ? var : 0.0) / sqrt((double)tm.sampN) * ms : 0.0;
        Log("streambuf: time #%ld secs=%.0f arenaMs: SetSize=%.1f Write=%.1f SetPosition=%.1f Scratch=%.1f Package=%.1f Assemble=%.1f other=%.1f frees=%.1f (requests %llu) "
            "ofWhichZeroFillMs: SetSize=%.1f Write=%.1f Scratch=%.1f Package=%.1f Assemble=%.1f "
            "passOverheadMs=%.1f (requests %llu; the game's allocator on them %.1f ms) growCopyMs=%.1f growCopyMB=%.1f copies=%llu sample: n=%llu meanUs=%.1f "
            "gamePathEstMs=%.0f +-%.0f (2 sigma, for the %llu served requests) perSecond: ownMs=%.3f gameEstMs=%.3f growCopyMs=%.3f",
            n, secs, tm.arena[1] * ms, tm.arena[2] * ms, tm.arena[3] * ms, tm.arena[4] * ms, tm.arena[5] * ms, tm.arena[6] * ms, (tm.arena[0] + tm.arena[7]) * ms,
            tm.arenaFree * ms, tm.arenaN, tm.zero[1] * ms, tm.zero[2] * ms, tm.zero[4] * ms, tm.zero[5] * ms, tm.zero[6] * ms, tm.pass * ms, tm.passN, tm.passGame * ms, tm.copy * ms, tm.copyB * MB, tm.copyN, tm.sampN, mean * ms * 1000.0, est, err,
            servedN, secs > 0 ? (arena + tm.pass) * ms / secs : 0.0, secs > 0 ? est / secs : 0.0, secs > 0 ? tm.copy * ms / secs : 0.0);
    }
    char line[400];
    int at = 0;
    for (DWORD i = 0; i < STB_SZ_N; i++)
        if (sz[i]) at += _snprintf_s(line + at, sizeof(line) - at, _TRUNCATE, " %s%lu%s=%llu", i == STB_SZ_N - 1 ? ">=" : "<", i < 8 ? 4ul << i : 1ul << (i < STB_SZ_N - 1 ? i - 8 : i - 9), i < 8 ? "K" : "M", sz[i]);
    if (at) Log("streambuf: sizes #%ld (requests by size)%s", n, line);
    static const char* const kHow[8] = {"?", "SetSize", "Write", "SetPosition", "Scratch", "Package", "Assemble", "?"};
    bool shown[STB_WHO_N] = {};
    for (int row = 0; row < 10; row++) {  // the ten requesters with the most bytes
        int best = -1;
        for (DWORD i = 0; i < STB_WHO_N; i++)
            if (who[i].n && !shown[i] && (best < 0 || who[i].bytes > who[best].bytes)) best = (int)i;
        if (best < 0) break;
        shown[best] = true;
        const StbWho& w = who[best];
        Log("streambuf: who #%ld at=0x%08lX via=0x%08lX how=%s n=%llu MB=%.1f big=%llu bigMB=%.1f maxKB=%lu grows=%lu served=%llu fresh=%llu "
            "fallback=%llu frees=%llu xthreadFrees=%llu liveN=%lu liveMB=%.1f peakMB=%.1f avgLifeMs=%.0f under1s=%.0f%%",
            n, w.at, w.via, kHow[w.how & 7], w.n, w.bytes * MB, w.big, w.bigB * MB, w.maxReq >> 10, w.grows, w.served, w.fresh, w.fallback, w.frees,
            w.xfree, w.liveN, w.liveB * MB, w.peakB * MB, w.frees ? (double)w.lifeMs / w.frees : 0.0, w.frees ? 100.0 * w.quick / w.frees : 0.0);
    }
    at = 0;
    for (DWORD i = 0; i < STB_TID_N; i++)
        if (tids[i].allocs || tids[i].frees) at += _snprintf_s(line + at, sizeof(line) - at, _TRUNCATE, " %lu:a=%llu,f=%llu", tids[i].tid, tids[i].allocs, tids[i].frees);
    if (at) Log("streambuf: threads #%ld (big blocks: thread id, allocated, freed; 0 = other threads)%s", n, line);
}

static bool ApplyStreamBuf(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"StreamBuf", L"Enabled", 0, ini) <= 0) return false;
    int mask = StbIniMask(ini);
    wcscpy_s(g_stbIni, ini);
    const int poolMB = GetPrivateProfileIntW(L"StreamBuf", L"PoolMB", 48, ini), minKB = GetPrivateProfileIntW(L"StreamBuf", L"MinKB", 128, ini);
    const int blkMB = GetPrivateProfileIntW(L"StreamBuf", L"MaxBlockMB", 8, ini), idle = GetPrivateProfileIntW(L"StreamBuf", L"IdleSec", 5, ini);
    const int minFree = GetPrivateProfileIntW(L"StreamBuf", L"MinFreeMB", 512, ini);
    g_stbBudget = (DWORD)(poolMB < 1 ? 1 : poolMB > 256 ? 256 : poolMB) << 20;
    g_stbMin = (DWORD)(minKB < 64 ? 64 : minKB > 65536 ? 65536 : minKB) << 10;
    g_stbMaxBlock = (DWORD)(blkMB < 1 ? 1 : blkMB > 64 ? 64 : blkMB) << 20;
    g_stbIdleMs = (DWORD)(idle < 0 ? 0 : idle > 3600 ? 3600 : idle) * 1000;
    g_stbMinFreeMB = (DWORD)(minFree < 0 ? 0 : minFree > 3072 ? 3072 : minFree);
    g_stbShared = GetPrivateProfileIntW(L"StreamBuf", L"Shared", 0, ini) > 0;
    const int gFloor = GetPrivateProfileIntW(L"StreamBuf", L"GrowFloor", 64, ini), gText = GetPrivateProfileIntW(L"StreamBuf", L"GrowTextFloor", 4096, ini);
    const int gFirst = GetPrivateProfileIntW(L"StreamBuf", L"GrowFirst", 0, ini);
    g_stbGrowFloor = (DWORD)(gFloor < 0 ? 0 : gFloor > 65536 ? 65536 : gFloor);
    g_stbGrowText = (DWORD)(gText < 0 ? 0 : gText > 65536 ? 65536 : gText);
    g_stbGrowFirst = (DWORD)(gFirst < 0 ? 0 : gFirst > 4096 ? 4096 : gFirst);
    if (!mask) { Log("stream buffer: Enabled=1 with Pool, Scratch, Package, Assemble, Grow, Timing and Stats all 0: nothing to do"); return false; }
    if (!StbInstallSites((u8*)(uintptr_t)STB_CTOR, (u8*)(uintptr_t)STB_REL, (u8*)(uintptr_t)STB_REALLOC, (const u8*)(uintptr_t)STB_SETSIZE,
                        (const u8*)(uintptr_t)STB_WRITE, (const u8*)(uintptr_t)STB_SETPOS))
        return false;
    g_stbInstalled = 1;
    // Scratch: the compressor's sites are written only when the ini asks for it at the start, [SaveCompress] (which
    // checks a hash of the same function) is off, and Apex Radiance does not hold the codec slot in front of it
    const char* scratch = "off";
    if (GetPrivateProfileIntW(L"StreamBuf", L"Scratch", 0, ini) > 0) {
        if (GetPrivateProfileIntW(L"SaveCompress", L"Enabled", 0, ini)) scratch = "not installed ([SaveCompress] is on and checks the same function)";
        else if (g_apexFound && g_apexStandDown) scratch = "not installed (Apex Radiance loaded)", InterlockedIncrement(&g_apexStood);
        else if ((g_stbScratchIn = StbInstallScratch((u8*)(uintptr_t)STB_COMP)) != false) scratch = "on (sites 0x4EC112 / 0x4EC12D / 0x4EC1B9 / 0x4EC1CE)";
        else scratch = "not installed (see above)";
    }
    // Package: the same rule (written only when asked for at the start). No Apex site and no site of ours lies in the
    // three functions (docs/68), so there is no stand-down
    const char* pkg = "off";
    if (GetPrivateProfileIntW(L"StreamBuf", L"Package", 0, ini) > 0) {
        u8* const fn[3] = {(u8*)(uintptr_t)STB_PKG_FN[0], (u8*)(uintptr_t)STB_PKG_FN[1], (u8*)(uintptr_t)STB_PKG_FN[2]};
        pkg = (g_stbPkgIn = StbInstallPackage(fn)) != false ? "on (sites 0x4A7204 / 0x4A72A5 / 0x4A834E / 0x4A89E3 / 0x4A8AE1 / 0x4A8AF1)" : "not installed (see above)";
    }
    // Assemble: as Package (no Apex site and no site of ours in the function)
    const char* asmS = "off";
    if (GetPrivateProfileIntW(L"StreamBuf", L"Assemble", 0, ini) > 0)
        asmS = (g_stbAsmIn = StbInstallAssemble((u8*)(uintptr_t)STB_ASM)) != false ? "on (sites 0x6169A8 / 0x616A93)" : "not installed (see above)";
    // Grow: its two call sites are written when Grow or Timing (which times Realloc's copies through one of them) is 1
    // at the start; what they do follows the run-time switches
    const char* growS = "off";
    if (mask & (STB_GROW | STB_TIME))
        growS = (g_stbGrowIn = StbInstallGrow((u8*)(uintptr_t)STB_WRITE, (u8*)(uintptr_t)STB_REALLOC)) == false ? "not installed (see above)" :
                (mask & STB_GROW) ? "on (sites 0x4DF05C / 0x4DEECB; floors 64 / text 4096 / first 0 unless the ini says otherwise)" : "sites 0x4DF05C / 0x4DEECB written for Timing, growth as the game's";
    if (!g_stbScratchIn) mask &= ~STB_SCRATCH;
    if (!g_stbPkgIn) mask &= ~STB_PACKAGE;
    if (!g_stbGrowIn) mask &= ~STB_GROW;
    if (!g_stbAsmIn) mask &= ~STB_ASSEMBLE;
    if (!(mask & STB_ARENA)) mask &= STB_STATS | STB_GROW | STB_TIME;
    if (mask & STB_TIME) g_stbTime.since = StbQpc();
    if (mask & STB_ARENA) StaReserve(g_stbBudget, g_stbMinFreeMB);  // before anything is armed; without it every request takes the game's path
    if (!g_stbZeroEvent) g_stbZeroEvent = CreateEventW(NULL, FALSE, FALSE, NULL);  // PreZero's wake; without it the thread wakes every 500 ms only
    HANDLE t = CreateThread(NULL, 64 * 1024, StbThread, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (t) CloseHandle(t);
    else Log("WARN stream buffer: no upkeep thread (%lu): no idle decommit, no run-time switch", GetLastError());
    InterlockedExchange(&g_stbArm, mask);
    Log("OK   stream buffer (docs/547): sites 0x4DEC0B / 0x4DEA99 / 0x4DEEF9 patched; Pool %s (arena %lu MB%s, requests %lu KB-%lu MB, idle %lu s, "
        "Zero %s, Verify %d), Scratch %s, Package %s, Assemble %s, Grow %s, Timing %s, PreZero %s, SkipClear %s, Shared %s, Stats %s%s (every \"diag: mem\" mark adds \"streambuf:\" lines)",
        (mask & STB_POOL) ? "on" : "off", g_stbBudget >> 20, g_sta.base ? "" : ", not reserved", g_stbMin >> 10, g_stbMaxBlock >> 20, g_stbIdleMs / 1000,
        (mask & STB_ZERO) ? "on" : "off", (mask & STB_VERIFY2) ? 2 : (mask & STB_VERIFY) ? 1 : 0, scratch, pkg, asmS, growS, (mask & STB_TIME) ? "on" : "off", (mask & STB_PREZERO) ? "on" : "off", (mask & STB_SKIPCLEAR) ? "on" : "off", g_sta.sec ? "on" : g_stbShared ? "asked, not in use" : "off", (mask & STB_STATS) ? "on" : "off",
        g_apexFound ? "; Apex Radiance loaded (no shared stream site)" : "");
    return true;
}
