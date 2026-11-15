// [Verify]: runtime checks of the mapping-doc inferences (docs/34-51). Included by ts3native.cpp (one translation unit).
// Measurement only: every probe counts or times and then runs the game's own code; nothing is changed or skipped.
//
// One generic pass-through stub per probe (like routegate.cpp): pushad/pushfd, VfEnter(probe, return slot), then the
// stolen prologue (trampoline) with the same registers, flags and stack, so no probe needs the function's signature.
// Probes that need the leave (timers, in-flight counts) swap the return address for VfLeaveThunk and keep the real one
// on a per-thread stack of 16; a frame left without returning (exception unwind) is dropped at the thread's next entry,
// because its return slot is at or below the new entry's. Handlers do integer work only (no x87, no SSE state kept),
// so float returns in ST0 (0xC2A570) pass through. The 10 FPS gate site 0xECAA65 and the main-loop site 0xECA9ED are
// mid-function: their own stubs touch only the flags, which they save.
// Prologues verified against the Steam 1.67 exe copy (capstone disassembly of game-copy/bin/TS3W.exe, 2026-10-15/16): each
// is whole instructions with no relative operand, the first SSE use of the function writes its register, and no branch
// targets the overwritten bytes. A probe whose bytes differ at install (another section, S3SS or a different exe already
// patched it) is skipped and logged; the two halves of a pair (shape swap + ray, key list + register, package probe +
// index find) are skipped together, since one half alone gives numbers that mean nothing.
// Thread roles: main = [0x11D8A88], sim = [0x11D8A8C] (game globals, 0 until set), other = neither; off-device = not the
// device owner [[0x11CECC8]+0x10] (the test of Device_IsOwnerThread 0x60E820 [verified asm]).
// shortcut: *Us totals are LONG microseconds and wrap after ~35 min of summed time; readers take deltas modulo 2^32.
// 0.10.8 (v22, docs/51 I-52..I-62): 31 probes 58-88 in the Threads group. Count-only kinds (no return swap) where only the
// entry matters: the cctor slow path 0xEA3382 is mid-function (after the fast exit's jne), the shape release counts only
// the swap's call (return address 0xC2A896), the four managed entries only count (and mark a lot add that runs managed
// code). Pump same pass: RunJobs 0x59A430 opens a pass per thread, ReadyJob 0x599FC0 inside it records the job, and an
// ExecuteJob 0x599720 of a recorded job in the same pass counts. Remote calls 0x7D98C0: inline when the caller already is
// the target thread (selector 1 = sim, 0 = main, the test at 0x7D98E2), a synchronous one opens a wait in which executed
// jobs and lot adds count. Not hookable at the entry: Job_Submit 0x59A350 (je in its first 4 bytes), RunJobsLocked
// 0x599E60 (0x599ED8 jumps to 0x599E65).

enum { VF_THREADS = 1, VF_RACES = 2, VF_COSTS = 4 };
enum {  // probe kinds
    VK_THREAD, VK_SWAP, VK_RAY, VK_KEYLIST, VK_REGDB, VK_IDLOOKUP, VK_PKG, VK_IDXFIND, VK_W2L, VK_FINDLOT, VK_TREE,
    VK_CONSUME, VK_FDBWAIT, VK_LOCK, VK_GATE,
    VK_COUNT, VK_SHAPEREL, VK_PUMP, VK_READY, VK_EXEC, VK_RCALL, VK_LOTADD, VK_MANAGED, VK_SHADER  // v22
};
// overlap groups (vfOverlap[g], group 4 in vfOverlapCur): a side-A entry while another thread is inside side B, or the
// reverse; a probe on both sides of a group counts two threads inside itself
enum { OV_SCENE = 1, OV_UI = 2, OV_R1 = 4, OV_ROUTE = 8, OV_CUR = 16, VF_NOV = 5 };
static const int VF_N = 89, VF_NT = 12, VF_NSLOT = 80, VF_NONE = 0xFF, VF_GATE = 26, VF_LOOP = 57;
static const int VF_SLOT_SWAP = 42, VF_SLOT_TREE = 43, VF_SLOT_JOBLOCK = 44, VF_SLOT_SIMSVCLOCK = 45;
// v22 slots that no single probe owns
static const int VF_SLOT_SHAPEREL = 56, VF_SLOT_READY = 58, VF_SLOT_SAMEPASS = 59, VF_SLOT_RCINLINE = 60, VF_SLOT_RCINLOTADD = 61,
                 VF_SLOT_JOBINWAIT = 62, VF_SLOT_LOTADDINWAIT = 63, VF_SLOT_LOTADDMANAGED = 66, VF_SLOT_INVOKE = 79;
static const DWORD VF_SWAP_RELEASE_RET = TS3A(0x00C2A896), VF_LOTADD_FN = TS3A(0x00AC1130), VF_WORLD_LOAD = TS3A(0x00C6D970),
                   VF_CCTOR_SLOW = TS3A(0x00EA3382), VF_SHADER_PS = TS3A(0x00799810), VF_THREADED_OBJS = TS3A(0x00D7F8B0);
struct VfProbe { const char* name; DWORD at; u8 len; u8 pro[10]; u8 kind, group, pair, thr, ovA, ovB; };
#define VFT(name, at, len, slot, ...) {name, at, len, {__VA_ARGS__}, VK_THREAD, VF_THREADS, VF_NONE, slot, 0, 0}
#define VFO(name, at, len, slot, a, b, ...) {name, at, len, {__VA_ARGS__}, VK_THREAD, VF_THREADS, VF_NONE, slot, a, b}
#define VFK(name, at, len, kind, slot, ...) {name, at, len, {__VA_ARGS__}, kind, VF_THREADS, VF_NONE, slot, 0, 0}
static const VfProbe kVf[VF_N] = {
    // Threads: per-thread calls and time (thread slot = probe index for 0-11)
    VFT("thumbTex", TS3A(0x00D5D910), 6, 0, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8),                        // docs/43
    VFT("texCreate", TS3A(0x00619540), 5, 1, 0x55, 0x8B, 0xEC, 0x6A, 0xFF),                             // docs/40
    VFT("texUpload", TS3A(0x0060D290), 9, 2, 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x78, 0x01, 0x00, 0x00),
    VFT("renderReq", TS3A(0x006F3DC0), 7, 3, 0x83, 0xEC, 0x10, 0x8B, 0x44, 0x24, 0x20),                 // docs/46
    VFK("execJob", TS3A(0x00599720), 7, VK_EXEC, 4, 0x53, 0x55, 0x56, 0x8B, 0x74, 0x24, 0x10),  // v22: also same pass, sync wait
    VFO("jobMainCull", TS3A(0x006F1B00), 6, 5, 0, OV_SCENE, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0),
    VFO("jobShadowCull", TS3A(0x006EBE30), 6, 6, 0, OV_SCENE, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0),
    VFO("jobReflCull", TS3A(0x006EA260), 6, 7, 0, OV_SCENE, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0),
    VFO("jobShadow2Cull", TS3A(0x006EBF50), 6, 8, 0, OV_SCENE, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0),
    VFT("jobSortMain", TS3A(0x006F1D10), 6, 9, 0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x10),
    VFT("texDrain", TS3A(0x0050A860), 5, 10, 0x83, 0xEC, 0x10, 0x53, 0x55),                            // docs/47
    VFT("asyncComplete", TS3A(0x007294F0), 8, 11, 0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x10),     // docs/39
    // Races
    {"shapeSwap", TS3A(0x00C2A840), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, VK_SWAP, VF_RACES, 13, VF_SLOT_SWAP, 0, 0},  // docs/38 C1
    {"ray", TS3A(0x00C2A570), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, VK_RAY, VF_RACES, 12, VF_NONE, 0, 0},
    {"keyList", TS3A(0x004B1AE0), 6, {0x83, 0xEC, 0x10, 0x53, 0x33, 0xC0}, VK_KEYLIST, VF_RACES, 15, VF_NONE, 0, 0},  // docs/39 R2
    {"registerDb", TS3A(0x004B2D00), 5, {0x83, 0xEC, 0x10, 0x53, 0x55}, VK_REGDB, VF_RACES, 14, VF_NONE, 0, 0},
    {"idLookup", TS3A(0x00939100), 8, {0x83, 0xEC, 0x08, 0x56, 0x8B, 0x74, 0x24, 0x14}, VK_IDLOOKUP, VF_RACES | VF_COSTS, VF_NONE, VF_NONE, 0, 0},
    // Costs
    {"pkgSource", TS3A(0x004AFFC0), 5, {0x51, 0x53, 0x55, 0x56, 0x57}, VK_PKG, VF_COSTS, 18, VF_NONE, 0, 0},       // docs/39 R1
    {"indexFind", TS3A(0x004AA1B0), 7, {0x83, 0xEC, 0x0C, 0x57, 0x8B, 0x79, 0x50}, VK_IDXFIND, VF_COSTS, 17, VF_NONE, 0, 0},
    {"worldToLocal", TS3A(0x00AB8540), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, VK_W2L, VF_COSTS, VF_NONE, VF_NONE, 0, 0},  // docs/38 C2
    {"findLot", TS3A(0x00C60D30), 5, {0x53, 0x8B, 0x5C, 0x24, 0x08}, VK_FINDLOT, VF_COSTS, VF_NONE, VF_NONE, 0, 0},  // docs/38 C3
    {"treeRebuild", TS3A(0x00712480), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, VK_TREE, VF_COSTS, VF_NONE, VF_SLOT_TREE, 0, 0},  // docs/48 S2
    {"fdbConsume", TS3A(0x00693780), 5, {0x83, 0xEC, 0x54, 0x53, 0x57}, VK_CONSUME, VF_COSTS, VF_NONE, VF_NONE, 0, 0},  // docs/41 S1
    {"fdbSync", TS3A(0x00593590), 5, {0x53, 0x56, 0x57, 0x8B, 0xF1}, VK_FDBWAIT, VF_COSTS, VF_NONE, VF_NONE, 0, 0},
    {"fdbPublish", TS3A(0x00593600), 7, {0x56, 0x57, 0x8B, 0xF1, 0x8D, 0x7E, 0x48}, VK_FDBWAIT, VF_COSTS, VF_NONE, VF_NONE, 0, 0},
    // docs/47 UI-L1, docs/36, docs/42: Lock_Acquire(this = lock, [esp+4] = timeout pointer); classified by that pointer
    {"lockAcquire", TS3A(0x004E16F0), 9, {0x53, 0x55, 0x56, 0x8B, 0xF1, 0x80, 0x7E, 0x24, 0x00}, VK_LOCK, VF_COSTS, VF_NONE, VF_NONE, 0, 0},
    {"fpsGate", TS3A(0x00ECAA65), 6, {0xD9, 0x05, 0x10, 0x5B, 0x0F, 0x01}, VK_GATE, VF_COSTS, VF_NONE, VF_NONE, 0, 0},  // docs/41 P5
    // v21 thread probes (docs/51 rows without a key in the first run); thread slots 12-41
    VFT("rayEntry", TS3A(0x006583E0), 6, 12, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0),                       // I-33-3
    VFO("routeRay", TS3A(0x00686CC0), 6, 13, OV_ROUTE, OV_ROUTE, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0),  // I-33-4
    VFT("stCtor", TS3A(0x007140D0), 6, 14, 0x83, 0xEC, 0x08, 0x0F, 0x57, 0xC0),                         // I-34-2
    VFT("stRender", TS3A(0x007135B0), 6, 15, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0),                       // I-34-3
    VFT("stWind", TS3A(0x00712F00), 6, 16, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0),                         // I-34-7
    VFT("audioCb", TS3A(0x00863DC0), 5, 17, 0xA1, 0x40, 0x8C, 0x1D, 0x01),                              // I-35-5
    VFT("threadedObjs", TS3A(0x00D7F8B0), 6, 18, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8),                   // I-36-1
    VFO("loadLayout", TS3A(0x00782BC0), 5, 19, 0, OV_UI, 0x83, 0xEC, 0x20, 0x53, 0x55),                // I-36-3, I-43-2
    VFT("rwfsWorker", TS3A(0x00401BE0), 7, 20, 0x53, 0x55, 0x56, 0x8B, 0x74, 0x24, 0x10),               // I-36-4
    VFT("poolWorker", TS3A(0x004E1DF0), 6, 21, 0x53, 0x56, 0x8B, 0x74, 0x24, 0x0C),                     // I-36-6
    VFT("devTexCreate", TS3A(0x00619770), 6, 22, 0x8B, 0x0D, 0xC8, 0xEC, 0x1C, 0x01),                   // I-37-3
    VFT("worldLoad", TS3A(0x00C6D970), 5, 23, 0x56, 0x8B, 0x74, 0x24, 0x08),                            // I-38-2
    VFT("lotGrid", TS3A(0x00C688A0), 6, 24, 0x83, 0xEC, 0x08, 0x56, 0x8B, 0xF1),                        // I-38-6
    VFK("remoteSync", TS3A(0x007D98C0), 6, VK_RCALL, 25, 0x51, 0x53, 0x56, 0x57, 0x8B, 0xF1),          // I-38-7; v22 inline, wait
    VFT("texFactory", TS3A(0x00722E00), 7, 26, 0x51, 0x53, 0x56, 0x8B, 0x74, 0x24, 0x10),               // I-40-1
    VFT("buildModel", TS3A(0x005AD8F0), 5, 27, 0x83, 0xEC, 0x20, 0x55, 0x56),                           // I-42-1
    VFT("buildStep", TS3A(0x005E2050), 5, 28, 0x51, 0x53, 0x56, 0x8B, 0xF1),                            // I-42-2
    VFT("lotLighting", TS3A(0x00ADD2C0), 6, 29, 0x53, 0x33, 0xDB, 0x56, 0x8B, 0xF1),                    // I-44-1
    VFT("ao", TS3A(0x006883A0), 6, 30, 0x81, 0xEC, 0xA0, 0x04, 0x00, 0x00),                             // I-44-2
    VFT("imposterStep", TS3A(0x00ADAE50), 6, 31, 0x51, 0x83, 0x7C, 0x24, 0x10, 0x04),                   // I-44-3, I-44-4
    VFT("idInsert", TS3A(0x00939170), 7, 32, 0x53, 0x55, 0x56, 0x8B, 0x74, 0x24, 0x14),                 // I-45-1
    VFO("sceneMsg", TS3A(0x00703CB0), 6, 33, OV_SCENE, 0, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0),         // I-46-1, I-48-3
    VFT("adoptVisuals", TS3A(0x0050B300), 7, 34, 0x53, 0x56, 0x8B, 0xF1, 0x8D, 0x5E, 0x38),             // I-47-2
    VFT("uiInput", TS3A(0x005989C0), 5, 35, 0x83, 0xEC, 0x2C, 0x53, 0x55),                              // I-47-3
    VFT("inputCall", TS3A(0x00598F90), 6, 36, 0x8B, 0x0D, 0xC0, 0xE2, 0x1C, 0x01),                      // I-47-7
    VFT("quadtree", TS3A(0x006E4770), 6, 37, 0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x2C),                       // I-48-1
    VFT("lotScene", TS3A(0x00ABFAC0), 10, 38, 0x83, 0xEC, 0x0C, 0x83, 0xB9, 0x64, 0x03, 0x00, 0x00, 0x00),  // I-50-2
    VFT("scriptMsg", TS3A(0x007D2DB0), 5, 39, 0x8B, 0x44, 0x24, 0x08, 0x56),                            // I-50-3
    VFO("wmDraw", TS3A(0x0050B270), 8, 40, OV_UI, OV_R1, 0x53, 0x56, 0x8B, 0xF1, 0x83, 0x7E, 0x2C, 0x00),  // I-43-2, I-47-5
    VFO("effectUpdate", TS3A(0x005172E0), 5, 41, OV_R1, 0, 0x83, 0xEC, 0x08, 0x53, 0x55),             // I-47-5
    // main loop iterations: the denominator of the gate (mid-function, flags only)
    {"fpsLoop", TS3A(0x00ECA9ED), 8, {0xF3, 0x0F, 0x10, 0x05, 0x9C, 0xDD, 0x1C, 0x01}, VK_GATE, VF_COSTS, VF_NONE, VF_NONE, 0, 0},
    // v22 (docs/51 I-52..I-62); thread slots 46-79. Mono warm-up set (docs/58 §4): by thread, must stay sim-only today
    VFT("monoLoaderLock", TS3A(0x00E85020), 5, 46, 0x68, 0xA8, 0x43, 0x1F, 0x01),                       // push loader_mutex
    VFT("monoVtable", TS3A(0x00EA1E30), 5, 47, 0x83, 0xEC, 0x30, 0x56, 0x57),
    VFK("monoCctorSlow", TS3A(0x00EA3382), 6, VK_COUNT, 48, 0x55, 0x33, 0xED, 0x56, 0x8B, 0x33),         // mid: past the fast exit
    VFT("monoThunk", TS3A(0x00E51400), 6, 49, 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x14),
    VFT("monoGcHandle", TS3A(0x00E78780), 7, 50, 0x83, 0xEC, 0x0C, 0x83, 0x7E, 0x08, 0x00),
    VFT("monoRtMethod", TS3A(0x00E53070), 5, 51, 0x68, 0x30, 0x41, 0x1F, 0x01),                         // push 0x11F4130
    // docs/60 §2 observe-only routing sites (gate sites 13-15, destroy drain, terrain graveyard)
    VFT("rtLotContains", TS3A(0x00628A90), 8, 52, 0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x14),
    VFT("rtExitBuildBuy", TS3A(0x00835240), 10, 53, 0x56, 0x8B, 0xF1, 0x80, 0xBE, 0x7B, 0x09, 0x00, 0x00, 0x00),
    VFT("rtRebuildLot", TS3A(0x00835110), 10, 54, 0x56, 0x8B, 0xF1, 0x80, 0xBE, 0x74, 0x09, 0x00, 0x00, 0x00),
    VFT("rtDestroy", TS3A(0x00939420), 5, 55, 0x83, 0xEC, 0x08, 0x53, 0x56),
    VFK("rtShapeRelease", TS3A(0x00682760), 5, VK_SHAPEREL, VF_NONE, 0x83, 0x41, 0x4C, 0xFF, 0x56),     // slot 56, swap calls only
    // docs/61: pump passes, jobs readied in them, script message drain, lot add, managed code entries
    VFK("evPump", TS3A(0x0059A430), 5, VK_PUMP, 57, 0x53, 0x56, 0x8B, 0xF1, 0x57),
    VFK("evReady", TS3A(0x00599FC0), 6, VK_READY, VF_NONE, 0x53, 0x56, 0x8B, 0x74, 0x24, 0x0C),         // slot 58
    VFT("evScriptDrain", TS3A(0x0076B1A0), 5, 64, 0x8B, 0x49, 0x10, 0x8B, 0x01),
    VFK("lotAddExec", TS3A(0x00ABD3C0), 7, VK_LOTADD, 65, 0x56, 0x8B, 0xF1, 0x0F, 0xB6, 0x4E, 0x19),     // fn 0xAC1130 only
    VFT("lotScore", TS3A(0x00C6C290), 6, 67, 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0),
    // docs/54, 55, 53
    VFT("glyphAddPage", TS3A(0x00452D30), 5, 68, 0x83, 0xEC, 0x08, 0x53, 0x55),
    VFT("glyphPageTex", TS3A(0x006916F0), 5, 69, 0x53, 0x8B, 0x5C, 0x24, 0x08),
    VFT("glyphClearPage", TS3A(0x00452630), 5, 70, 0x83, 0xEC, 0x0C, 0x53, 0x55),
    VFT("stblRebuild", TS3A(0x0059BDE0), 5, 71, 0x83, 0xEC, 0x34, 0x55, 0x57),
    VFT("padPoll", TS3A(0x0049C5F0), 6, 72, 0x81, 0xEC, 0xE8, 0x00, 0x00, 0x00),
    VFK("shaderVs", TS3A(0x00799780), 6, VK_SHADER, 73, 0x53, 0x55, 0x8B, 0x6C, 0x24, 0x0C),
    VFK("shaderPs", TS3A(0x00799810), 6, VK_SHADER, 74, 0x53, 0x55, 0x8B, 0x6C, 0x24, 0x0C),
    VFT("loadPump", TS3A(0x00ECA350), 10, 75, 0x83, 0xEC, 0x08, 0xF6, 0x05, 0xD8, 0xFB, 0x1F, 0x01, 0x01),
    VFO("cursorApply", TS3A(0x00505620), 5, 76, OV_CUR, OV_CUR, 0x53, 0x55, 0x57, 0x8B, 0xF9),
    // docs/57: interpreter frame arena grow and GC_free by thread (count only)
    VFK("arenaGrow", TS3A(0x00D71900), 5, VK_COUNT, 77, 0x8B, 0x44, 0x24, 0x04, 0x55),
    VFK("gcFree", TS3A(0x00E4BEB0), 5, VK_COUNT, 78, 0x56, 0x8B, 0x74, 0x24, 0x08),
    // native -> managed entries (slot 79; inside a lot add also slot 66)
    VFK("invokeState", TS3A(0x00E59FD0), 6, VK_MANAGED, VF_NONE, 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x60),
    VFK("runtimeInvoke", TS3A(0x00E5A600), 6, VK_MANAGED, VF_NONE, 0x81, 0xEC, 0x50, 0x01, 0x00, 0x00),
    VFK("nativeToManaged", TS3A(0x00D8A8E0), 5, VK_MANAGED, VF_NONE, 0x8B, 0x44, 0x24, 0x18, 0x53),
    VFK("invokeDelegate", TS3A(0x00D75FA0), 5, VK_MANAGED, VF_NONE, 0x55, 0x8D, 0x6C, 0x24, 0x98),
};
#undef VFT
#undef VFO
#undef VFK
// Lock_Acquire timeout pointers (each a static -1, one per module) [verified: the only push sites of each are in that module]
static const DWORD VF_UI_LOCK = TS3A(0x00FBA5CC), VF_JOB_LOCK = TS3A(0x00FD9744), VF_SIMSVC_LOCK = TS3A(0x00FE4DC4), VF_ID_BUCKETS = 0x409;

// game globals; the selftest points them at its own
static const DWORD* g_vfMainTidSlot = (const DWORD*)(uintptr_t)TS3A(0x011D8A88);
static const DWORD* g_vfSimTidSlot = (const DWORD*)(uintptr_t)TS3A(0x011D8A8C);
static u8* const* g_vfDevSlot = (u8* const*)(uintptr_t)TS3A(0x011CECC8);      // GraphicsDevice wrapper, owner tid +0x10
static u8* const* g_vfDevCfgSlot = (u8* const*)(uintptr_t)TS3A(0x011CDD10);   // DeviceConfig singleton (getter 0x58E3F0, creator 0x591010)
static u8* const* g_vfResMgrSlot = (u8* const*)(uintptr_t)TS3A(0x011C78D8);   // resource manager (docs/08)
static u8* const* g_vfSimSvcSlot = (u8* const*)(uintptr_t)TS3A(0x011CE6A0);   // SimService (getter 0x5EB7C0, docs/42)
static const LONG* g_vfIcNestSlot = (const LONG*)(uintptr_t)TS3A(0x011F2D84); // EA IC nesting counter (docs/56 §2)
static volatile LONG g_vfWorldLoaded;                                     // 0xC6D970 entered once (shader phase)

static void* g_vfTramp[VF_N];
static LONGLONG g_vfFreq = 1;
static int g_vfGroups;
static volatile LONG g_vfRayIn, g_vfKeyIn, g_vfRegIn;  // threads inside 0xC2A570 / GetKeyList / RegisterDatabase
static volatile LONG g_vfOvIn[VF_NOV][2];              // threads inside each overlap group side
static volatile LONG g_vfTreeFrame;                    // tree rebuilds since the last FrameDb consume
static volatile LONG g_vfRayK, g_vfIdK, g_vfW2lK;      // low 10 bits of the K counters
static volatile LONG g_vfSeen[VF_NSLOT];               // thread slots: roles already logged (1 main, 2 sim, 4 other, 8 off-device)
static volatile LONG g_vfOverlapLogs;
static SRWLOCK g_vfRetLock = SRWLOCK_INIT;
static HANDLE g_vfLog = INVALID_HANDLE_VALUE;
static SRWLOCK g_vfLogLock = SRWLOCK_INIT;
static HANDLE g_vfPoll;

// extra: package walk mark (VK_PKG), lock class (VK_LOCK), 1 = synchronous wait (VK_RCALL); ov: overlap groups entered
// (side A bits, side B bits << 8)
struct VfFrame { DWORD* slot; DWORD ret; LONGLONG t0; LONG extra; u8 probe, role; WORD ov; };
// pumpDepth / lotAddDepth / syncDepth: open RunJobs passes, lot adds, synchronous remote-call waits on this thread;
// ready[]: jobs readied inside the current outermost pass (newest 16)
struct VfThread {
    int n; LONG rayDepth, keyDepth, regDepth, pkgDepth, walked; LONG ov[VF_NOV][2];
    LONG pumpDepth, lotAddDepth, syncDepth, nReady; DWORD ready[16]; VfFrame f[16];
};
static __declspec(thread) VfThread t_vf;

static inline LONG VfUs(LONGLONG dt) { return (LONG)(dt * 1000000 / g_vfFreq); }
static inline volatile LONG* VfOvCount(int g) { return g < 4 ? &ts3native_counters.vfOverlap[g] : &ts3native_counters.vfOverlapCur; }
static inline void VfMax(volatile LONG* m, LONG v) {
    for (LONG o = *m; v > o; o = *m)
        if (InterlockedCompareExchange(m, v, o) == o) return;
}
static inline void VfAddK(volatile LONG* low, LONG* k) {  // counts in units of 1024 without a 64-bit counter
    if ((InterlockedIncrement(low) & 1023) == 0) InterlockedIncrement(k);
}
static inline int VfRole(DWORD tid) {  // 0 main, 1 sim, 2 other
    if (tid == *g_vfMainTidSlot) return 0;
    return *g_vfSimTidSlot && tid == *g_vfSimTidSlot ? 1 : 2;
}
// thread slot s: 6 counters (main, sim, other, offDevice calls, us total, max us); 0-11 in v20, 12-45 in v21, 46-79 in v22
static inline LONG* VfSlot(int s) {
    Ts3nCounters& k = ts3native_counters;
    return s < VF_NT ? k.vfThread[s] : s < 46 ? k.vfThread2[s - VF_NT] : k.vfThread3[s - 46];
}

static void VfLogLine(const char* fmt, ...) {
    if (g_vfLog == INVALID_HANDLE_VALUE) return;
    char buf[512];
    int n = _snprintf_s(buf, sizeof(buf), _TRUNCATE, "{\"t\":%llu,", LsUnixMs());
    va_list ap;
    va_start(ap, fmt);
    const int m = n < 0 ? -1 : _vsnprintf_s(buf + n, sizeof(buf) - n - 2, _TRUNCATE, fmt, ap);
    va_end(ap);
    if (m < 0) { InterlockedIncrement(&ts3native_counters.vfLogDropped); return; }
    n += m;
    buf[n++] = '}';
    buf[n++] = '\n';
    DWORD w;
    AcquireSRWLockExclusive(&g_vfLogLock);
    if (!WriteFile(g_vfLog, buf, (DWORD)n, &w, NULL)) InterlockedIncrement(&ts3native_counters.vfLogDropped);
    ReleaseSRWLockExclusive(&g_vfLogLock);
}

static const char* const kVfRoles[4] = {"main", "sim", "other", "offDevice"};

static void VfThreadEnter(int slot, const char* name, DWORD tid, int role, DWORD ret) {
    LONG* s = VfSlot(slot);
    InterlockedIncrement(&s[role]);
    LONG bits = 1 << role;
    const u8* dev = *g_vfDevSlot;
    if (dev && tid != *(const DWORD*)(dev + 0x10)) { InterlockedIncrement(&s[3]); bits |= 8; }
    if ((g_vfSeen[slot] & bits) == bits) return;
    const LONG old = InterlockedOr(&g_vfSeen[slot], bits);
    for (int r = 0; r < 4; r++)
        if ((bits & ~old) & (1 << r))
            VfLogLine("\"kind\":\"first\",\"probe\":\"%s\",\"role\":\"%s\",\"tid\":%lu,\"ret\":\"0x%08X\"", name, kVfRoles[r], tid, ret);
}

static void VfIdOffSim(DWORD tid, DWORD ret) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.vfIdOffSim);
    if (k.vfIdOffSimRet[7]) return;  // 8 distinct callers recorded
    AcquireSRWLockExclusive(&g_vfRetLock);
    int i = 0;
    while (i < 8 && k.vfIdOffSimRet[i] && (DWORD)k.vfIdOffSimRet[i] != ret) i++;
    const bool add = i < 8 && !k.vfIdOffSimRet[i];
    if (add) k.vfIdOffSimRet[i] = (LONG)ret;
    ReleaseSRWLockExclusive(&g_vfRetLock);
    if (add) VfLogLine("\"kind\":\"idOffSim\",\"tid\":%lu,\"role\":\"%s\",\"ret\":\"0x%08X\"", tid, kVfRoles[VfRole(tid)], ret);
}

// ObjectId_Lookup 0x939100 [verified asm]: ecx = bucket array (service +0x10), [esp+8] = id pointer, bucket id_lo % 0x409,
// chain through node +0x10, ids at node +8/+0xC; object count at bucket array +0x1028 (service +0x1038). Sim thread only:
// the walk reads the unlocked hash just as the lookup does.
static void VfIdChain(const DWORD* slot) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD* buckets = (const DWORD*)(uintptr_t)slot[-2];
    const DWORD* id = (const DWORD*)(uintptr_t)slot[2];
    if (!buckets || !id) return;
    const DWORD lo = id[0], hi = id[1];
    const DWORD* node = (const DWORD*)(uintptr_t)buckets[lo % VF_ID_BUCKETS];
    LONG steps = 0;
    for (; node && steps < 64; node = (const DWORD*)(uintptr_t)node[4]) {
        steps++;
        if (node[2] == lo && node[3] == hi) break;
    }
    k.vfIdChain[steps == 0 ? 0 : steps == 1 ? 1 : steps < 4 ? 2 : steps < 8 ? 3 : steps < 16 ? 4 : 5]++;
    if (steps > k.vfIdMaxChain) k.vfIdMaxChain = steps;
    k.vfIdObjects = *(const LONG*)((const u8*)buckets + 0x1028);
}

// overlap groups: count against the other side (or this probe's own side for a self group), then enter the sides
static WORD VfOvEnter(VfThread& t, const VfProbe& p, DWORD tid, DWORD ret) {
    const u8 groups = p.ovA | p.ovB;
    if (!groups || !(g_vfGroups & VF_RACES)) return 0;
    for (int g = 0; g < VF_NOV; g++) {
        if (!(groups & (1 << g))) continue;
        const int other = (p.ovA & (1 << g)) ? 1 : 0;
        if (g_vfOvIn[g][other] - t.ov[g][other] > 0) {
            InterlockedIncrement(VfOvCount(g));
            if (InterlockedIncrement(&g_vfOverlapLogs) <= 16)
                VfLogLine("\"kind\":\"overlap\",\"group\":%d,\"probe\":\"%s\",\"tid\":%lu,\"ret\":\"0x%08X\"", g, p.name, tid, ret);
        }
        for (int s = 0; s < 2; s++)
            if (((s ? p.ovB : p.ovA) >> g) & 1) { InterlockedIncrement(&g_vfOvIn[g][s]); t.ov[g][s]++; }
    }
    return (WORD)(p.ovA | p.ovB << 8);
}

static void VfUnwind(VfThread& t, const VfFrame& f) {  // in-flight state of a frame that ends (left or dropped)
    switch (kVf[f.probe].kind) {
        case VK_RAY: InterlockedDecrement(&g_vfRayIn); t.rayDepth--; break;
        case VK_KEYLIST: InterlockedDecrement(&g_vfKeyIn); t.keyDepth--; break;
        case VK_REGDB: InterlockedDecrement(&g_vfRegIn); t.regDepth--; break;
        case VK_PKG: t.pkgDepth--; break;
        case VK_PUMP: t.pumpDepth--; break;
        case VK_LOTADD: t.lotAddDepth--; break;
        case VK_RCALL: if (f.extra == 1) t.syncDepth--; break;
    }
    for (int g = 0; g < VF_NOV && f.ov; g++)
        for (int s = 0; s < 2; s++)
            if ((f.ov >> (g + 8 * s)) & 1) { InterlockedDecrement(&g_vfOvIn[g][s]); t.ov[g][s]--; }
}

static void __cdecl VfLeaveThunk();

static void VfPush(VfThread& t, int id, DWORD* slot, int role, LONG extra, WORD ov) {
    VfFrame& f = t.f[t.n++];
    f.slot = slot;
    f.ret = *slot;
    f.probe = (u8)id;
    f.role = (u8)role;
    f.extra = extra;
    f.ov = ov;
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    f.t0 = q.QuadPart;
    *slot = (DWORD)(uintptr_t)VfLeaveThunk;
}

// Lock_Acquire 0x4E16F0 [verified asm]: ecx = the lock, [esp+4] = pointer to the timeout (a static -1 per module, which
// tells the caller's module apart): 0 = UI (WindowManager, 10 sites), 1 = JobManager, 2 = SimService, -1 = not counted
static inline int VfLockClass(const DWORD* slot) {
    const DWORD a = slot[1];
    return a == VF_UI_LOCK ? 0 : a == VF_JOB_LOCK ? 1 : a == VF_SIMSVC_LOCK ? 2 : -1;
}

// RemoteMethodCall_Execute 0xABD3C0 [verified asm]: ecx = the call object (pushad slot -2), fn at +0x10
static inline bool VfIsLotAdd(const DWORD* slot) {
    const DWORD* call = (const DWORD*)(uintptr_t)slot[-2];
    return call && call[4] == VF_LOTADD_FN;
}

static void __cdecl VfEnter(int id, DWORD* slot) {
    if (t_snapRun) return;  // P4 clone run: pass through (docs/94 §12.4.3)
    Ts3nCounters& k = ts3native_counters;
    if (k.vfMode != 1) return;
    const VfProbe& p = kVf[id];
    const int lock = p.kind == VK_LOCK ? VfLockClass(slot) : -1;
    if (p.kind == VK_LOCK && lock < 0) return;  // every engine lock comes here: only the three classified ones count
    if (p.kind == VK_SHAPEREL && *slot != VF_SWAP_RELEASE_RET) return;  // every shape release comes here: the swap's only
    if (p.kind == VK_LOTADD && !VfIsLotAdd(slot)) return;               // every remote method call: the lot add only
    if (p.at == VF_WORLD_LOAD) g_vfWorldLoaded = 1;
    VfThread& t = t_vf;
    while (t.n && t.f[t.n - 1].slot <= slot) VfUnwind(t, t.f[--t.n]);  // frames left without returning
    const DWORD tid = GetCurrentThreadId();
    const int role = VfRole(tid);
    // mid-function 0xEA3382 (after sub esp,0x18; push ebx): the return address is 0x1C above
    const DWORD ret = p.at == VF_CCTOR_SLOW ? slot[7] : *slot;
    const bool timed = (p.thr != VF_NONE && p.kind != VK_COUNT) || p.kind == VK_RAY || p.kind == VK_KEYLIST || p.kind == VK_REGDB ||
                       p.kind == VK_PKG || p.kind == VK_CONSUME || p.kind == VK_FDBWAIT || p.kind == VK_LOCK;
    if (timed && t.n == 16) { InterlockedIncrement(&k.vfUntracked); return; }
    LONG extra = t.walked;
    switch (p.kind) {
        // v22
        case VK_SHAPEREL: VfThreadEnter(VF_SLOT_SHAPEREL, p.name, tid, role, ret); return;
        case VK_READY:  // ReadyJob(job) inside a pass: remember the job
            if (t.pumpDepth > 0 && slot[1]) {
                t.ready[t.nReady++ & 15] = slot[1];
                VfThreadEnter(VF_SLOT_READY, "readyInPump", tid, role, ret);
            }
            return;
        case VK_PUMP:
            if (t.pumpDepth++ == 0) t.nReady = 0;  // outermost: a new pass
            break;
        case VK_EXEC: {  // ExecuteJob(job): readied earlier in this pass, or run while this thread waits on a sync call
            const DWORD job = slot[1];
            for (int i = 0; job && t.pumpDepth > 0 && i < 16 && i < t.nReady; i++)
                if (t.ready[i] == job) {
                    t.ready[i] = 0;
                    VfThreadEnter(VF_SLOT_SAMEPASS, "pumpSamePass", tid, role, ret);
                    break;
                }
            if (t.syncDepth > 0) VfThreadEnter(VF_SLOT_JOBINWAIT, "jobInSyncWait", tid, role, ret);
            break;
        }
        case VK_RCALL: {  // RemoteCall_Dispatch(selector, async): selector 1 = sim, 0 = main [verified 0x7D98CC-0x7D98E9]
            const DWORD target = (u8)slot[1] ? *g_vfSimTidSlot : *g_vfMainTidSlot;
            const bool inl = target == tid;
            if (inl) {
                VfThreadEnter(VF_SLOT_RCINLINE, "rcInline", tid, role, ret);
                if (t.lotAddDepth > 0) VfThreadEnter(VF_SLOT_RCINLOTADD, "rcInLotAdd", tid, role, ret);
            }
            extra = !inl && !(u8)slot[2];  // posted and waited for
            t.syncDepth += extra;
            break;
        }
        case VK_LOTADD:
            if (t.syncDepth > 0) VfThreadEnter(VF_SLOT_LOTADDINWAIT, "lotAddInSyncWait", tid, role, ret);
            t.lotAddDepth++;
            break;
        case VK_MANAGED:
            VfThreadEnter(VF_SLOT_INVOKE, "managedEntry", tid, role, ret);
            if (t.lotAddDepth > 0) VfThreadEnter(VF_SLOT_LOTADDMANAGED, "lotAddManaged", tid, role, ret);
            return;
        case VK_SHADER: if (!g_vfWorldLoaded) InterlockedIncrement(&k.vfShaderStartup[p.at == VF_SHADER_PS]); break;
        // v20/v21
        case VK_SWAP:
            InterlockedIncrement(&k.vfSwaps);
            if (g_vfRayIn - t.rayDepth > 0) {
                InterlockedIncrement(&k.vfSwapsInRay);
                if (InterlockedIncrement(&g_vfOverlapLogs) <= 16)
                    VfLogLine("\"kind\":\"swapInRay\",\"tid\":%lu,\"rays\":%ld,\"ret\":\"0x%08X\"", tid, g_vfRayIn - t.rayDepth, *slot);
            }
            break;
        case VK_RAY:
            VfAddK(&g_vfRayK, &k.vfRayCallsK);
            VfMax(&k.vfRayMaxInFlight, InterlockedIncrement(&g_vfRayIn));
            t.rayDepth++;
            break;
        case VK_KEYLIST:
            InterlockedIncrement(&k.vfKeyLists);
            InterlockedIncrement(&g_vfKeyIn);
            t.keyDepth++;
            if (g_vfRegIn - t.regDepth > 0) InterlockedIncrement(&k.vfKeyListOverlaps);
            break;
        case VK_REGDB:
            InterlockedIncrement(&k.vfRegDbs);
            InterlockedIncrement(&g_vfRegIn);
            t.regDepth++;
            if (g_vfKeyIn - t.keyDepth > 0) {
                InterlockedIncrement(&k.vfKeyListOverlaps);
                if (InterlockedIncrement(&g_vfOverlapLogs) <= 16)
                    VfLogLine("\"kind\":\"registerInKeyList\",\"tid\":%lu,\"keyLists\":%ld,\"ret\":\"0x%08X\"", tid, g_vfKeyIn - t.keyDepth, *slot);
            }
            break;
        case VK_IDLOOKUP:
            VfAddK(&g_vfIdK, &k.vfIdLookupsK);
            if (role == 1) { if (g_vfGroups & VF_COSTS) VfIdChain(slot); }
            else if ((g_vfGroups & VF_RACES) && *g_vfSimTidSlot) VfIdOffSim(tid, *slot);
            return;
        case VK_PKG: t.pkgDepth++; break;
        case VK_IDXFIND: if (t.pkgDepth) t.walked++; return;
        case VK_W2L: VfAddK(&g_vfW2lK, &k.vfW2lCallsK); return;
        case VK_FINDLOT: InterlockedIncrement(&k.vfFindLotCalls); return;
        case VK_TREE: InterlockedIncrement(&k.vfTreeRebuilds); InterlockedIncrement(&g_vfTreeFrame); break;
        case VK_CONSUME: if (role == 0) VfMax(&k.vfTreeMaxPerFrame, InterlockedExchange(&g_vfTreeFrame, 0)); break;
        case VK_FDBWAIT: if (id == 24 && role == 1) InterlockedIncrement(&k.vfFdbPublishes); break;
        case VK_LOCK:
            extra = lock;
            if (lock) VfThreadEnter(lock == 1 ? VF_SLOT_JOBLOCK : VF_SLOT_SIMSVCLOCK, lock == 1 ? "jobLock" : "simSvcLock", tid, role, *slot);
            break;
        default: break;
    }
    if (p.thr != VF_NONE) VfThreadEnter(p.thr, p.name, tid, role, ret);
    if (timed) VfPush(t, id, slot, role, extra, VfOvEnter(t, p, tid, ret));
}

// For an owner stub that ran VfEnter first (routegate.cpp, rtsites.cpp, the terrain graveyard): the caller behind our leave thunk
// (any open frame: probe P1b reads the slot of a caller further up, lotfd.cpp)
static DWORD VfRealRet(const DWORD* slot) {
    const VfThread& t = t_vf;
    if (*slot == (DWORD)(uintptr_t)VfLeaveThunk)
        for (int i = t.n - 1; i >= 0; i--) if (t.f[i].slot == slot) return t.f[i].ret;
    return *slot;
}

// docs/68 §6 rule 3: another section's stub owns this probe's address; it calls VfEnter(probe) itself (slot = index + 1)
static volatile LONG* VfOwnerSlot(DWORD at) {
    for (int s = 0; s < RG_SITES; s++) if (g_rgTramp[s] && kRgSites[s].addr == at) return &g_rgVf[s];
    for (int s = 0; s < RT_N; s++) if (g_rtTramp[s] && kRtSites[s].addr == at) return &g_rtVf[s];
    if (g_tgTramp && at == TG_RELEASE) return &g_tgVf;
    return NULL;
}

static DWORD __cdecl VfLeave() {
    Ts3nCounters& k = ts3native_counters;
    VfThread& t = t_vf;
    const VfFrame f = t.f[--t.n];
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    const LONG us = VfUs(q.QuadPart - f.t0);
    VfUnwind(t, f);
    const VfProbe& p = kVf[f.probe];
    if (p.thr != VF_NONE) {
        LONG* s = VfSlot(p.thr);
        InterlockedExchangeAdd(&s[4], us);
        VfMax(&s[5], us);
    }
    switch (p.kind) {
        case VK_PKG:
            if (t.pkgDepth) break;  // outermost call only
            InterlockedIncrement(&k.vfPkgCalls);
            InterlockedExchangeAdd(&k.vfPkgUs, us);
            VfMax(&k.vfPkgMaxUs, us);
            InterlockedExchangeAdd(&k.vfPkgWalked, t.walked - f.extra);
            VfMax(&k.vfPkgMaxWalked, t.walked - f.extra);
            break;
        case VK_CONSUME:
            if (f.role != 0) break;
            InterlockedIncrement(&k.vfFdbHolds);
            InterlockedExchangeAdd(&k.vfFdbHoldUs, us);
            VfMax(&k.vfFdbHoldMaxUs, us);
            break;
        case VK_FDBWAIT:
            if (f.role != 1) break;
            InterlockedIncrement(&k.vfFdbWaits);
            InterlockedExchangeAdd(&k.vfFdbWaitUs, us);
            VfMax(&k.vfFdbWaitMaxUs, us);
            break;
        case VK_LOCK: {
            LONG* s = f.extra == 0 ? k.vfUiLock[f.role] : NULL;
            if (s) {
                InterlockedIncrement(&s[0]);
                InterlockedExchangeAdd(&s[1], us);
                VfMax(&s[2], us);
            } else {
                s = VfSlot(f.extra == 1 ? VF_SLOT_JOBLOCK : VF_SLOT_SIMSVCLOCK);
                InterlockedExchangeAdd(&s[4], us);
                VfMax(&s[5], us);
            }
            break;
        }
        default: break;
    }
    return f.ret;
}

// The probe returned here (ret or ret N): back to its real caller with eax, edx, flags and the x87 stack as it left them.
static __declspec(naked) void __cdecl VfLeaveThunk() {
    __asm {
        push eax  // becomes the return address
        pushad
        pushfd
        call VfLeave
        mov [esp + 36], eax
        popfd
        popad
        ret
    }
}

#define VF_STUB(i) static __declspec(naked) void VfStub##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call VfEnter __asm add esp, 8 __asm popfd __asm popad __asm jmp dword ptr [g_vfTramp + i * 4] }
VF_STUB(0) VF_STUB(1) VF_STUB(2) VF_STUB(3) VF_STUB(4) VF_STUB(5) VF_STUB(6) VF_STUB(7) VF_STUB(8) VF_STUB(9) VF_STUB(10)
VF_STUB(11) VF_STUB(12) VF_STUB(13) VF_STUB(14) VF_STUB(15) VF_STUB(16) VF_STUB(17) VF_STUB(18) VF_STUB(19) VF_STUB(20)
VF_STUB(21) VF_STUB(22) VF_STUB(23) VF_STUB(24) VF_STUB(25) VF_STUB(27) VF_STUB(28) VF_STUB(29) VF_STUB(30) VF_STUB(31)
VF_STUB(32) VF_STUB(33) VF_STUB(34) VF_STUB(35) VF_STUB(36) VF_STUB(37) VF_STUB(38) VF_STUB(39) VF_STUB(40) VF_STUB(41)
VF_STUB(42) VF_STUB(43) VF_STUB(44) VF_STUB(45) VF_STUB(46) VF_STUB(47) VF_STUB(48) VF_STUB(49) VF_STUB(50) VF_STUB(51)
VF_STUB(52) VF_STUB(53) VF_STUB(54) VF_STUB(55) VF_STUB(56) VF_STUB(58) VF_STUB(59) VF_STUB(60) VF_STUB(61) VF_STUB(62)
VF_STUB(63) VF_STUB(64) VF_STUB(65) VF_STUB(66) VF_STUB(67) VF_STUB(68) VF_STUB(69) VF_STUB(70) VF_STUB(71) VF_STUB(72)
VF_STUB(73) VF_STUB(74) VF_STUB(75) VF_STUB(76) VF_STUB(77) VF_STUB(78) VF_STUB(79) VF_STUB(80) VF_STUB(81) VF_STUB(82)
VF_STUB(83) VF_STUB(84) VF_STUB(85) VF_STUB(86) VF_STUB(87) VF_STUB(88)
// 0xECAA65 sits inside the main loop FUN_00ECA960 with ST0 and possibly SSE registers live: no call, flags saved, one locked add
static __declspec(naked) void VfStub26() {
    __asm {
        pushfd
        cmp ts3native_counters.vfMode, 1
        jne skip
        lock inc ts3native_counters.vfFpsGate
    skip:
        popfd
        jmp dword ptr [g_vfTramp + 26 * 4]
    }
}
// 0xECA9ED: every main-loop iteration passes here (both branches of the gate's flag test join); xmm0 is written by the
// moved movss itself, so nothing is live across it but the flags
static __declspec(naked) void VfStub57() {
    __asm {
        pushfd
        cmp ts3native_counters.vfMode, 1
        jne skip
        lock inc ts3native_counters.vfFpsLoops
    skip:
        popfd
        jmp dword ptr [g_vfTramp + 57 * 4]
    }
}
static void (*const kVfStubs[VF_N])() = {
    VfStub0, VfStub1, VfStub2, VfStub3, VfStub4, VfStub5, VfStub6, VfStub7, VfStub8, VfStub9, VfStub10, VfStub11, VfStub12,
    VfStub13, VfStub14, VfStub15, VfStub16, VfStub17, VfStub18, VfStub19, VfStub20, VfStub21, VfStub22, VfStub23, VfStub24,
    VfStub25, VfStub26, VfStub27, VfStub28, VfStub29, VfStub30, VfStub31, VfStub32, VfStub33, VfStub34, VfStub35, VfStub36,
    VfStub37, VfStub38, VfStub39, VfStub40, VfStub41, VfStub42, VfStub43, VfStub44, VfStub45, VfStub46, VfStub47, VfStub48,
    VfStub49, VfStub50, VfStub51, VfStub52, VfStub53, VfStub54, VfStub55, VfStub56, VfStub57, VfStub58, VfStub59, VfStub60,
    VfStub61, VfStub62, VfStub63, VfStub64, VfStub65, VfStub66, VfStub67, VfStub68, VfStub69, VfStub70, VfStub71, VfStub72,
    VfStub73, VfStub74, VfStub75, VfStub76, VfStub77, VfStub78, VfStub79, VfStub80, VfStub81, VfStub82, VfStub83, VfStub84,
    VfStub85, VfStub86, VfStub87, VfStub88};

// Readouts, no hooks: game globals read under SEH (a pointer may be stale or not created yet).
// SpeedTree's counter [0x11D8628] is not read: it is a net of unpaired adds and subtracts (one free path subtracts 0x13800
// where the alloc adds 0x13810, frees subtract a 4-byte header plus count*0x54) and read -10 MB, flat, in both worlds
// [verified asm + measured 2026-10-15]; vfSpeedTreeKB stays -1 (no value).
static void VfPollOnce() {
    Ts3nCounters& k = ts3native_counters;
    k.vfSpeedTreeKB = -1;
    __try {
        // DeviceConfig 0x591010 -> DeviceConfig_Init 0x5928F0 -> hw info init 0x613CB0(this + 0x174) stores the VRAM MB from
        // the DxDiag path 0x612640 (MB string -> ((MB + 4) << 20) & ~8 MB >> 20, so 8192 MB wraps to 0) at hw info +0xC4
        // (0x613EAE) [verified asm 2026-10-16]: 0 here is the game's own value, not a wrong offset
        const u8* dc = *g_vfDevCfgSlot;
        k.vfVramMB = dc ? *(const LONG*)(dc + 0x174 + 0xC4) : -1;
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.vfVramMB = -1; }
    __try {
        const u8* dev = *g_vfDevSlot;
        k.vfDeviceTid = dev ? *(const LONG*)(dev + 0x10) : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.vfDeviceTid = -1; }
    __try {
        const u8* s = *g_vfSimSvcSlot;
        k.vfSimSlice = s ? s[0x139] : -1;
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.vfSimSlice = -1; }
    __try {  // as tools/probe/agent.js resCache(): cache array +0xA0/+0xA4 of {cache*, priority}; ResourceCache vtable 0xFFDD94.
        // ResourceSystem init 0x738E2E creates AnimationCache (priority 1000, budget forced 0xF00000) before the ResourceCache
        // (priority 0, budget from the config, 0x738EDA) [verified asm]: read the priority-0 instance only
        const u8* m = *g_vfResMgrSlot;
        LONG bytes = 0, flush = 0, budget = -1;
        if (m && (*(const DWORD*)m == TS3A(0xFFE250) || *(const DWORD*)m == TS3A(0xFB2DA0))) {
            const DWORD* b = *(const DWORD* const*)(m + 0xA0);
            const DWORD* e = *(const DWORD* const*)(m + 0xA4);
            for (int i = 0; b + 2 * i < e && i < 16; i++) {
                const u8* c = (const u8*)(uintptr_t)b[2 * i];
                if (!c || b[2 * i + 1] != 0 || *(const DWORD*)c != TS3A(0xFFDD94)) continue;
                bytes = (LONG)(*(const DWORD*)(c + 0x94) >> 10);
                flush = (LONG)(*(const DWORD*)(c + 0x98) >> 10);
                budget = (LONG)(*(const DWORD*)(c + 0x8C) >> 20);
                break;
            }
        }
        k.vfRcBytesKB = bytes;
        k.vfRcFlushKB = flush;
        k.vfRcBudgetMB = budget;
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.vfRcBudgetMB = -2; }
    // v22: EA IC nesting counter [0x11F2D84] (docs/56 §2, role inferred): a nesting depth is never negative
    __try {
        const LONG nest = *g_vfIcNestSlot;
        VfMax(&k.vfMonoIcNestMax, nest);
        if (nest < 0) InterlockedIncrement(&k.vfMonoIcNestNeg);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static DWORD WINAPI VfPollThread(void* memory) {
    for (int i = 0;; i++) {
        VfPollOnce();
        const Ts3nCounters& k = ts3native_counters;
        if (memory && i % 10 == 0)
            VfLogLine("\"kind\":\"mem\",\"vramMB\":%ld,\"rcBytesKB\":%ld,\"rcFlushKB\":%ld,\"rcBudgetMB\":%ld,\"simSlice\":%ld",
                      k.vfVramMB, k.vfRcBytesKB, k.vfRcFlushKB, k.vfRcBudgetMB, k.vfSimSlice);
        Sleep(1000);
    }
}

// One probe at `at` with prologue `pro` of the probe's length: the game address and its verified bytes, or a selftest dummy.
static bool VfInstallAt(int id, DWORD at, const u8* pro) {
    char name[48];
    sprintf_s(name, "%s (verify)", kVf[id].name);
    return (g_vfTramp[id] = InstallDetour(name, at, pro, kVf[id].len, (void*)kVfStubs[id], &g_vfTramp[id])) != NULL;
}

// [Verify] Enabled=1 (master) with Threads / Races / Costs / Memory = 1 per group; Mode = start value of vfMode;
// Log=1 writes <asi>-verify.jsonl (first thread role per probe, off-sim id lookups, overlaps, memory every 10 s).
static bool ApplyVerify(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"Verify", L"Enabled", 0, ini)) { Log("verify disabled (Enabled=0)"); return false; }
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_vfFreq = f.QuadPart;
    g_vfGroups = (GetPrivateProfileIntW(L"Verify", L"Threads", 0, ini) ? VF_THREADS : 0) |
                 (GetPrivateProfileIntW(L"Verify", L"Races", 0, ini) ? VF_RACES : 0) |
                 (GetPrivateProfileIntW(L"Verify", L"Costs", 0, ini) ? VF_COSTS : 0);
    const bool memory = GetPrivateProfileIntW(L"Verify", L"Memory", 0, ini) != 0;
    // 0xD7F8B0 is S3PP's SimulatorStageProfiler seam (docs/68 §4 #1): our jmp there disarms all four of its seams and
    // its type-lookup speed-up, so the probe needs its own switch
    const bool threadedObjs = GetPrivateProfileIntW(L"Verify", L"ThreadedObjects", 0, ini) != 0;
    bool wanted[VF_N], ok[VF_N];
    volatile LONG* owner[VF_N];
    char hex[64];
    for (int i = 0; i < VF_N; i++) {
        const VfProbe& p = kVf[i];
        wanted[i] = (p.group & g_vfGroups) && (p.at != VF_THREADED_OBJS || threadedObjs);
        if (wanted[i] && ApexVerifySite(p.at)) {
            char what[64];
            sprintf_s(what, "[Verify] probe %s", p.name);
            if (ApexStandsDown(ApexVerifySite(p.at), what, "its entry")) wanted[i] = false;
        }
        if ((p.group & g_vfGroups) && !wanted[i]) Log("verify %s off (ThreadedObjects=0: S3PP's stage profiler owns 0x%08X)", p.name, p.at);
        ok[i] = wanted[i] && Matches((const void*)(uintptr_t)p.at, p.pro, p.len);
        owner[i] = wanted[i] && !ok[i] && p.pair == VF_NONE ? VfOwnerSlot(p.at) : NULL;
        if (owner[i]) Log("verify %s: counted inside the stub that owns 0x%08X", p.name, p.at);
        else if (wanted[i] && !ok[i]) {
            if (Readable((const void*)(uintptr_t)p.at, p.len)) Hex((const u8*)(uintptr_t)p.at, p.len, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
            Log("SKIP verify %s: prologue at 0x%08X differs (found %s)", p.name, p.at, hex);
        }
    }
    bool pairOk[VF_N];
    for (int i = 0; i < VF_N; i++) pairOk[i] = ok[i] && (kVf[i].pair == VF_NONE || ok[kVf[i].pair]);
    for (int i = 0; i < VF_N; i++)
        if (ok[i] && !pairOk[i]) Log("SKIP verify %s: its pair %s is not hooked", kVf[i].name, kVf[kVf[i].pair].name);
    k.vfMode = GetPrivateProfileIntW(L"Verify", L"Mode", 1, ini) == 0 ? 0 : 1;
    if ((g_vfGroups || memory) && GetPrivateProfileIntW(L"Verify", L"Log", 1, ini)) {
        wchar_t path[MAX_PATH];
        wcscpy_s(path, ini);
        wchar_t* dot = wcsrchr(path, L'.');
        if (dot) *dot = 0;
        wcscat_s(path, L"-verify.jsonl");
        g_vfLog = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (g_vfLog == INVALID_HANDLE_VALUE) Log("verify: cannot create %ls (err %lu), no event log", path, GetLastError());
    }
    LONG ready = 0, ready2 = 0, ready3 = 0;
    int n = 0, want = 0;
    for (int i = 0; i < VF_N; i++) {
        want += wanted[i];
        if (owner[i]) *owner[i] = i + 1;
        if (owner[i] || (pairOk[i] && VfInstallAt(i, kVf[i].at, kVf[i].pro))) {
            if (i < 27) ready |= 1 << i; else if (i < 58) ready2 |= 1 << (i - 27); else ready3 |= 1 << (i - 58);
            n++;
        }
    }
    if ((memory || (g_vfGroups & VF_COSTS)) && (g_vfPoll = CreateThread(NULL, 64 * 1024, VfPollThread, (void*)(uintptr_t)memory,
                                                                         STACK_SIZE_PARAM_IS_A_RESERVATION, NULL)) != NULL)
        ready |= 1 << 28;
    k.vfReady = ready;
    k.vfReady2 = ready2;
    k.vfReady3 = ready3;
    Log("%s verify: %d of %d probes hooked (groups 0x%X, memory %d), ready 0x%lX 0x%lX 0x%lX, Mode=%ld, event log %s", n == want ? "OK  " : "PART",
        n, want, g_vfGroups, memory ? 1 : 0, ready, ready2, ready3, k.vfMode, g_vfLog != INVALID_HANDLE_VALUE ? "on" : "off");
    return ready != 0 || ready2 != 0 || ready3 != 0;
}
