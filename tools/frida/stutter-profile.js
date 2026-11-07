// Sim-thread stutter profiler for TS3W.exe (Steam build, SHA-256 10b2423e...0609).
// Times what the simulation loop (MonoScriptHost::Simulate, 0xD81840) spends per iteration:
// script tick, route searches, GC stop-the-world marking, finalizers, catch-up steps, animation (two passes), locomotion,
// render publish, and the four scheduler seams
// (ScriptTick 0xD80BC0, TaskObjects 0xD7FC60, OneShotObjects 0xD74800, ThreadedObjects 0xD7F8B0; docs/04 F17) as
// separate buckets, so each hitch is split by stage. S3PP's SimulatorStageProfiler detours the same prologues: turn it
// off in Sims3Performance.ini while profiling (the byte check refuses to hook a detoured prologue).
// Addresses and reasons: docs/04-re-findings.md.
//
// Run (game already in Live mode):
//   frida -n TS3W.exe -l tools/frida/stutter-profile.js
//   frida -n TS3W.exe -l tools/frida/stutter-profile.js -P '{"out":"C:/Users/Public/ts3prof","hitchMs":50}'
// Output: <out>-seconds.csv (one row per second), <out>-hitches.jsonl (one line per slow iteration, `n` = iteration number)
// and <out>-freezes.jsonl (one line per sim-to-render publish gap >= hitchMs: what the player sees as frozen Sims, docs/162).
// Hitch lines also carry `tasks` (the 5 slowest script runs >= taskMinMs of that iteration: stage, name, ms, resume/yield
// frames) and `taskRuns` (runs per stage), timed per run by a CModule with no thread suspended (docs/166; -P tasks:false = off).
// Default <out> is ts3prof-<timestamp> in the game's working directory (Game\Bin).

'use strict';

const HOOKS = {
  // name: [address, expected first bytes]
  simulate: [0xd81840, '81 ec 88 00 00 00 53 55'],
  scriptTick: [0xd80bc0, '55 8b ec 83 e4 f8 81 ec'],
  // the other scheduler seams, siblings of scriptTick inside Simulate (docs/04 F17); each runs at most once per iteration
  taskObjects: [0xd7fc60, '55 8b ec 83 e4 f8 83 ec'],
  oneShot: [0xd74800, '83 ec 10 53 55 56 57 8b'],
  threaded: [0xd7f8b0, '55 8b ec 83 e4 f8 81 ec'],
  catchUpSteps: [0xd70730, '83 ec 1c 53 55 56 57 89'],
  gcTryToCollect: [0xe4a050, '83 3d 94 30 1f 01 00 74'],
  gcStoppedMark: [0xe49980, '51 e8 1a 78 00 00 a1 60'],
  gcInvokeFinalizers: [0xe697f0, 'e8 eb 1b fe ff 85 c0 74'],
  routeOuter: [0x82c6e0, '81 ec d8 00 00 00 53 56'], // lock + setup + search + unlock, returns success in AL
  routeSearch: [0x81b2a0, '55 8b ec 83 e4 f0 81 ec'], // the A* search itself
  // script entry points, from tools/internal-calls-map.ts (the table is {fn, name})
  Route_Plan: [0x765160, '0f 57 c0 8b 0d c4 40 1d'],
  Route_Replan: [0x765490, '8b 0d c4 40 1d 01 83 ec'],
  Route_PlanFromPoint: [0x765240, '55 8b ec 83 e4 f0 0f 57'],
  Route_ReplanFromPosition: [0x765550, '55 8b ec 83 e4 f0 8b 0d'],
  Route_IsPointRoutable: [0x7538c0, '55 8b ec 83 e4 f0 8b 0d'],
  // the rest of Simulate's untimed share (docs/28 G12): two animation passes (docs/22 §1: call 0xD81A70 -> SACS driver,
  // time step, rig pose; call 0xD81C73 -> time step, rig pose 0x940430), locomotion, and the sim-to-render publish
  animSacs: [0x571ad0, '53 56 8b f1 83 4e 28 08'],
  animPose: [0x571b90, '83 ec 08 55 57 8b f9 8b'],
  locomotion: [0x83ced0, '55 8b ec 83 e4 f0 81 ec'],
  publish: [0x593590, '53 56 57 8b f1 8d 7e 48'], // triple-buffer sync (EnterCriticalSection); may nest in other stages
};
// sim-thread ms per stage, summed like the scheduler seams
const EXTRA_STAGES = ['animSacs', 'animPose', 'locomotion', 'publish'];
const ROUTE_ENTRIES = ['Route_Plan', 'Route_Replan', 'Route_PlanFromPoint', 'Route_ReplanFromPosition', 'Route_IsPointRoutable'];

// tasks: per-task attribution (docs/166, below); taskMinMs: shortest task run kept in a hitch line
let cfg = { out: null, hitchMs: 50, tasks: true, taskMinMs: 2 };

// --- high-resolution clock (QPC), in ms ---
const k32 = Process.getModuleByName('kernel32.dll');
const qpc = new NativeFunction(k32.getExportByName('QueryPerformanceCounter'), 'int', ['pointer']);
const qpf = new NativeFunction(k32.getExportByName('QueryPerformanceFrequency'), 'int', ['pointer']);
const qbuf = Memory.alloc(8);
qpf(qbuf);
const ticksPerMs = Number(qbuf.readS64()) / 1000;
function now() { qpc(qbuf); return Number(qbuf.readS64()) / ticksPerMs; }

// --- per-iteration and per-second accumulators (sim thread only, except routeOtherThreads) ---
function blankIter() {
  return { script: 0, gcMark: 0, gcMarks: 0, gcAborted: 0, gcForced: 0, gcForcedMs: 0, fin: 0, finRun: 0,
           steps: 0, route: 0, routeSearch: 0, routes: 0, routeFails: 0, byEntry: {},
           taskObjects: 0, oneShot: 0, threaded: 0, animSacs: 0, animPose: 0, locomotion: 0, publish: 0,
           // ts3native [HitchAttr] (docs/96 §9): sim waits for the route job, ms by class, count, longest and its key
           rtLock: 0, rtOtherLock: 0, rtJob: 0, rtPlanWait: 0, rtWaits: 0, rtMaxMs: 0, rtMaxKey: '',
           // [MovedWaits] (docs/178 §9): waits a read-only skip moved (same job), MW_* switch mask, longest and its key
           rtMovedMs: 0, rtMovedN: 0, rtMovedSw: 0, rtMovedMaxMs: 0, rtMovedKey: '',
           // [HitchAttr] Holders=1 (docs/165 §8): sim/main lock waits >= MinMs with the holder (lockHoldersFrame), longest first
           holders: [] };
}
function blankSecond() {
  return { iters: 0, maxIterMs: 0, hitches: 0, script: 0, gcMark: 0, gcMarks: 0, gcAborted: 0, gcForced: 0,
           gcForcedMs: 0, maxGcMarkMs: 0, fin: 0, finRun: 0, maxSteps: 0, route: 0, routeSearch: 0, routes: 0,
           routeFails: 0, maxRouteMs: 0, routeOtherThreads: 0,
           taskObjects: 0, oneShot: 0, threaded: 0, maxTaskObjectsMs: 0,
           animSacs: 0, animPose: 0, locomotion: 0, publish: 0,
           // ts3native [LotStream] (docs/28 §5.5); 0 when the plugin or the section is off
           lotAdds: 0, lotAddMs: 0, lotAddMaxMs: 0, lotAddObjs: 0, lotRemoves: 0, lotRemoveMs: 0, pumpMaxMs: 0, lotScoreMs: 0,
           lotSetActive: 0,
           // P-clock (docs/85 §6): 1 when Simulate's paused copy was set at the start of any iteration of this second; the
           // step accumulator host+0xC14 at the start of the last iteration (left over from the previous one's steps;
           // -1 = P-clock off or no iteration this second; NaN/Infinity = docs/85 H6)
           simPaused: 0, stepAcc: -1,
           // ts3native [HitchAttr] (docs/96 §9), ms per second; 0 when the plugin or the section is off
           rtLock: 0, rtOtherLock: 0, rtJob: 0, rtPlanWait: 0, rtWaits: 0, // new columns go last: readers use the header
           // FrameDb_PublishFrame 0x593600 calls on the sim thread (docs/162): 0 or 1 per iteration; longest publish gap ending here
           publishes: 0, maxPubGapMs: 0 };
}

// Sim-to-render publish (docs/162 §2): Simulate calls 0x76C8A0 -> FrameDb_PublishFrame once per iteration at most (0xD81DF5 after
// the steps, 0xD81E9E when paused; an unpaused iteration with 0 steps publishes nothing). The renderer interpolates between the two
// newest published frames, so the time between two publishes is how long the Sims stand still on screen. Not in HOOKS: a
// mismatch only turns the freeze log off instead of stopping the whole profiler.
const PUBLISH_FRAME = [0x593600, '56 57 8b f1 8d 7e 48 57'];
let iterNo = 0, lastPub = 0, prevPubGap = 0, itersSincePub = 0;

// P-clock: Simulate's step branch reads its paused copy through `mov ecx, edi; call 0x8A6D10` at 0xD81B38
// (0x8A6D10 = `mov al, [ecx+0x10]; ret`, edi = host+0xA64) after `fstp [esi+0xC14]`. Never hook inside Simulate: it
// never returns, and a Frida hook there (the 2026-10-17 call-site hook) keeps the script from unloading, so the
// profiler outlived its -t window in every run (tools/probe/frida-unload-test.py). Instead the existing
// GC_try_to_collect hook reads the same fields once per iteration: Simulate keeps host in esi from 0xD81849 on and calls
// it directly at 0xD819AA, so at that entry esi = host, paused copy = host+0xA74, accumulator = host+0xC14.
const CLOCK_CHECKS = [[0xd81849, '8b f1'], [0xd819aa, 'e8 a1 86 0c 00'], [0xd81a46, '8d be 64 0a 00 00'],
                      [0xd81b32, 'd9 9e 14 0c 00 00 e8 d3 51 b2 ff'], [0x8a6d10, '8a 41 10 c3']];
const CLOCK_RET = ptr(0xd819af);
let clockOn = false;

// ts3native [LotStream] counters, v19 byte offsets (docs/registry.md; the names match tools/probe/agent.js nativeCounters).
// Totals become per-second deltas; the *MaxUs fields hold the max since the last reset, so they are read and zeroed here.
const LS_OFF = { lotAdds: 0x2cc, lotAddUs: 0x2d0, lotAddMaxUs: 0x2d4, lotAddObjs: 0x2d8, lotRemoves: 0x2e0, lotRemoveUs: 0x2e4,
                 pumpMaxUs: 0x2f4, lotScoreUs: 0x304, lotSetOn: 0x310, lotSetOff: 0x314 };
let lsCounters = null, lsPrev = null; // undefined once known absent
function lotStreamSecond() {
  if (lsCounters === undefined) return;
  if (lsCounters === null) {
    const m = Process.findModuleByName('ts3native.asi');
    const c = m === null ? null : m.findExportByName('ts3native_counters');
    lsCounters = c !== null && c.readS32() >= 19 && (c.add(0x2c4).readS32() & 31) === 31 ? c : undefined; // lsReady: all five hooks
    console.log(`[ts3prof] ts3native lot stream counters ${lsCounters ? 'read every second' : 'not active (columns stay 0)'}`);
    if (lsCounters === undefined) return;
  }
  const cur = Object.fromEntries(Object.entries(LS_OFF).map(([k, o]) => [k, lsCounters.add(o).readS32()]));
  for (const k of ['lotAddMaxUs', 'pumpMaxUs']) lsCounters.add(LS_OFF[k]).writeS32(0);
  if (lsPrev) {
    const d = k => cur[k] - lsPrev[k];
    Object.assign(sec, { lotAdds: d('lotAdds'), lotAddMs: d('lotAddUs') / 1000, lotAddMaxMs: cur.lotAddMaxUs / 1000, lotAddObjs: d('lotAddObjs'),
      lotRemoves: d('lotRemoves'), lotRemoveMs: d('lotRemoveUs') / 1000, pumpMaxMs: cur.pumpMaxUs / 1000, lotScoreMs: d('lotScoreUs') / 1000,
      lotSetActive: d('lotSetOn') + d('lotSetOff') });
  } // the first read only sets the baseline (its maxima date from the game start)
  lsPrev = cur;
}
// ts3native [HitchAttr] frame block `ts3native_hitchattr` (hitchattr.cpp HaFrame): version, enabled, us[4], n, maxUs, maxKey.
// Read and zeroed at each iteration boundary on the sim thread, the only thread that writes it, so no clock fit is needed.
let haBlock = null; // undefined once known absent
const HA_ZERO = new ArrayBuffer(28), HA_ZERO2 = new ArrayBuffer(48);
function hitchAttrFrame() {
  if (haBlock === undefined) return;
  if (haBlock === null) {
    const m = Process.findModuleByName('ts3native.asi');
    const e = m === null ? null : m.findExportByName('ts3native_hitchattr');
    haBlock = e !== null && e.readS32() >= 1 && e.add(4).readS32() === 1 ? e : undefined;
    console.log(`[ts3prof] ts3native hitch attribution ${haBlock ? 'read every iteration' : 'not active (rt* fields stay 0)'}`);
    if (haBlock === undefined) return;
  }
  const len = haBlock.readS32() >= 2 ? 48 : 28; // v2 (docs/178 §9 [MovedWaits]): mvUs, mvN, mvSw, mvMaxUs, mvKey
  const v = new Int32Array(haBlock.add(8).readByteArray(len));
  haBlock.add(8).writeByteArray(len === 48 ? HA_ZERO2 : HA_ZERO);
  iter.rtLock += v[0] / 1000; iter.rtOtherLock += v[1] / 1000; iter.rtJob += v[2] / 1000; iter.rtPlanWait += v[3] / 1000;
  iter.rtWaits += v[4];
  if (v[5] / 1000 > iter.rtMaxMs) { iter.rtMaxMs = v[5] / 1000; iter.rtMaxKey = '0x' + (v[6] >>> 0).toString(16).padStart(8, '0'); }
  if (len === 48 && v[8]) {
    iter.rtMovedMs += v[7] / 1000; iter.rtMovedN += v[8]; iter.rtMovedSw |= v[9];
    if (v[10] / 1000 >= iter.rtMovedMaxMs) { iter.rtMovedMaxMs = v[10] / 1000; iter.rtMovedKey = '0x' + (v[11] >>> 0).toString(16).padStart(8, '0'); }
  }
}
// ts3native [HitchAttr] Holders=1 ring `ts3native_lockholders` (hitchattr.cpp HoRing, docs/165 §8 gap #2): version, enabled,
// minUs, head, 64 x 32-byte records {seq, waiter 0 sim / 1 main, lock, us, holderTid, holderRole, holderSite, waiterSite}.
// Records written since the last iteration boundary go to this iteration's `holders` (sim and main waits, longest first).
let hoRing = null, hoSeen = 0; // undefined once known absent
const HO_ROLE = ['sim', 'main', 'route worker', 'replay consumer', 'thread'];
const hex8 = x => '0x' + (x >>> 0).toString(16).padStart(8, '0');
function lockHoldersFrame() {
  if (hoRing === undefined) return;
  if (hoRing === null) {
    const m = Process.findModuleByName('ts3native.asi');
    const e = m === null ? null : m.findExportByName('ts3native_lockholders');
    hoRing = e !== null && e.readS32() >= 1 && e.add(4).readS32() === 1 ? e : undefined;
    console.log(`[ts3prof] ts3native lock holders ${hoRing ? 'read every iteration' : 'not active (holders stay empty)'}`);
    if (hoRing === undefined) return;
    hoSeen = hoRing.add(12).readS32();
  }
  const head = hoRing.add(12).readS32();
  for (let i = Math.max(hoSeen + 1, head - 63); i <= head; i++) {
    const r = new Int32Array(hoRing.add(16 + ((i - 1) & 63) * 32).readByteArray(32));
    if (r[0] !== i) continue; // being written, or overwritten by a later record
    iter.holders.push({ who: r[1] ? 'main' : 'sim', ms: r[3] / 1000, lock: hex8(r[2]), by: HO_ROLE[r[5]] ?? 'thread', tid: r[4] >>> 0,
                        site: hex8(r[6]), at: hex8(r[7]) });
  }
  hoSeen = head;
  iter.holders.sort((a, b) => b.ms - a.ms);
  iter.holders.length = Math.min(iter.holders.length, 5);
}
// --- per-task attribution (docs/166): which script task or object a slow iteration ran, with no thread suspended ---
// A CModule times every task run inside the four scheduler seams on the sim thread and keeps the 5 slowest runs of the
// iteration; JS names them only for hitch lines, at the iteration boundary on the sim thread (GC_try_to_collect entry).
//   taskRun 0xD7F000: thiscall(host, &entry, bool, bool), resumes one TaskObjects/ThreadedObjects task; entry +8 = task state
//     ([0] MonoMethod, [1] task object, +0x140 depth of the saved frames or -1, cells at +0x3C + 4 * depth, cell +4 = MonoMethod)
//   scriptInvoke 0xEA1090: mono_runtime_invoke(method, obj, params, exc) = `jmp [0x1195B60]`; ScriptTick and OneShotObjects
//     call it once per object
//   yieldSave 0xE53620: cdecl(state, records, method), the frame save of a yielding task (docs/130 §1.6); read at its return
// Only the outermost run inside a seam is timed (a nested invoke from an IC inside a task belongs to that task).
const TASK_HOOKS = {
  taskRun: [0xd7f000, '83 ec 1c 53 55 56 57 8b'],
  scriptInvoke: [0xea1090, 'ff 25 60 5b 19 01 cc cc'],
  yieldSave: [0xe53620, '83 ec 08 8b 4c 24 0c 8b'],
};
const FIELD_FROM_NAME = 0xe6f720; // mono_class_get_field_from_name(klass, name), cdecl; walks the parents (docs/123 §8.6)
const TASK_STAGES = [null, 'script', 'taskObjects', 'oneShot', 'threaded']; // stage ids the seam hooks write
const NTOP = 8; // docs/247 §9: 8 slowest runs per iteration (was 5); a slot only costs its memory
const TASK_C = `
#include <gum/guminterceptor.h>
#define NTOP ${NTOP}
/* from[3..5], to[3..5] = frames 4-6 (docs/247 §9): fromMore / toMore in the hitch line; from / to stay 3 deep for the readers */
typedef struct { gint64 ticks; guint32 stage, kind; void * obj, * klass, * method; void * from[3], * to[3], * fromMore[3], * toMore[3]; } Slot;
typedef struct { gint64 t0; guint32 on, stage; void * obj, * klass, * method, * state; void * from[6]; } Inv;
extern int __attribute__((stdcall)) QueryPerformanceCounter (gint64 * t);
/* in JS-allocated memory (CModule data is not writable on Windows, frame-times.js) */
extern volatile guint32 st[16]; /* simTid, stage, open, nTop, dropped, yieldSeen, slotSize, -, runs[5] at 8.. */
extern volatile gint64 minTicks;
extern Slot top[NTOP];
extern void * yieldTo[6];
#define SIM st[0]
#define STAGE st[1]
#define OPEN st[2]
#define NT st[3]

static void frames (char * state, void ** out) {
  int d = *(int *) (state + 0x140), i;
  for (i = 0; i < 6; i++) out[i] = NULL;
  for (i = 0; i < 6 && d - i >= 0 && d - i < 63; i++) {
    char * cell = *(char **) (state + 0x3c + 4 * (d - i));
    if (cell == NULL) break;
    out[i] = *(void **) (cell + 4);
  }
}
static int begin (GumInvocationContext * ic, Inv * v) {
  v->on = 0;
  if (OPEN || STAGE == 0 || SIM == 0 || gum_invocation_context_get_thread_id (ic) != SIM) return 0;
  OPEN = 1; v->on = 1; v->stage = STAGE; st[5] = 0;
  v->obj = v->klass = v->method = v->state = NULL; v->from[0] = v->from[1] = v->from[2] = v->from[3] = v->from[4] = v->from[5] = NULL;
  return 1;
}
static void end (Inv * v, guint32 kind) {
  gint64 t1; Slot * s; int i, m;
  if (!v->on) return;
  QueryPerformanceCounter (&t1);
  OPEN = 0;
  if (v->stage < 5) st[8 + v->stage]++;
  t1 -= v->t0;
  if (t1 < minTicks) return;
  if (NT < NTOP) s = &top[NT++];
  else {
    for (m = 0, i = 1; i < NTOP; i++) if (top[i].ticks < top[m].ticks) m = i;
    st[4]++;
    if (top[m].ticks >= t1) return;
    s = &top[m];
  }
  s->ticks = t1; s->stage = v->stage; s->kind = kind; s->obj = v->obj; s->klass = v->klass; s->method = v->method;
  for (i = 0; i < 3; i++) { s->from[i] = v->from[i]; s->to[i] = st[5] ? yieldTo[i] : NULL; s->fromMore[i] = v->from[3 + i]; s->toMore[i] = st[5] ? yieldTo[3 + i] : NULL; }
}
void onEnterRun (GumInvocationContext * ic) {
  Inv * v = GUM_IC_GET_INVOCATION_DATA (ic, Inv);
  char ** pe; char * entry, * state;
  if (!begin (ic, v)) return;
  pe = (char **) gum_invocation_context_get_nth_argument (ic, 0);
  entry = pe != NULL ? *pe : NULL;
  state = entry != NULL ? *(char **) (entry + 8) : NULL;
  if (state != NULL) {
    v->method = *(void **) state;
    v->obj = *(void **) (state + 4);
    if (v->obj != NULL) v->klass = **(void ***) v->obj; /* live task object: vtable +0 = klass */
    frames (state, v->from); /* the resume point: cells are freed when the run resumes them */
  }
  QueryPerformanceCounter (&v->t0);
}
void onLeaveRun (GumInvocationContext * ic) { end (GUM_IC_GET_INVOCATION_DATA (ic, Inv), 1); }
void onEnterInvoke (GumInvocationContext * ic) {
  Inv * v = GUM_IC_GET_INVOCATION_DATA (ic, Inv);
  if (!begin (ic, v)) return;
  v->method = gum_invocation_context_get_nth_argument (ic, 0);
  v->obj = gum_invocation_context_get_nth_argument (ic, 1); /* not read here: JS checks it at the boundary */
  QueryPerformanceCounter (&v->t0);
}
void onLeaveInvoke (GumInvocationContext * ic) { end (GUM_IC_GET_INVOCATION_DATA (ic, Inv), 2); }
void onEnterYield (GumInvocationContext * ic) {
  Inv * v = GUM_IC_GET_INVOCATION_DATA (ic, Inv);
  v->on = OPEN && gum_invocation_context_get_thread_id (ic) == SIM;
  v->state = gum_invocation_context_get_nth_argument (ic, 0);
}
void onLeaveYield (GumInvocationContext * ic) {
  Inv * v = GUM_IC_GET_INVOCATION_DATA (ic, Inv);
  if (!v->on || v->state == NULL) return;
  frames ((char *) v->state, yieldTo); /* the cells this call just wrote */
  st[5] = 1;
}
int slotSize (void) { return sizeof (Slot); }
`;
let tasks = null; // { cm, st, top, listeners } once installed
const TASK_ZERO = new ArrayBuffer(64);
const taskNames = new Map(); // pointer -> name (class and method metadata live for the whole run)
const fieldOffs = new Map(); // klass:field -> instance offset or -1
let fieldFromName = null, fieldNameStr = {};
// metadata names end at the first non-printable byte
function cstr(p) {
  if (p.isNull()) return '';
  const b = new Uint8Array(p.readByteArray(120)); let e = 0;
  while (e < b.length && b[e] >= 0x20 && b[e] < 0x7f) e++;
  return String.fromCharCode(...b.subarray(0, e));
}
function klassName(k) { // MonoClass name +0x34, namespace +0x38 (verified live by the probe's /taskstall)
  const key = 'k' + k;
  if (taskNames.has(key)) return taskNames.get(key);
  let n = null;
  try { const cn = cstr(k.add(0x34).readPointer()), ns = cstr(k.add(0x38).readPointer()); n = /^[\w`<>$]+$/.test(cn) ? (ns ? ns + '.' : '') + cn : null; } catch (e) { /* not a class */ }
  if (n) taskNames.set(key, n); // failures are retried
  return n;
}
function methodName(m) { // MonoMethod klass +8, name +0x18
  const key = 'm' + m;
  if (taskNames.has(key)) return taskNames.get(key);
  let n = null;
  try { const c = klassName(m.add(8).readPointer()), mn = cstr(m.add(0x18).readPointer()); n = c && /^[\w.`<>$|]+$/.test(mn) ? `${c}.${mn}` : null; } catch (e) { /* not a method */ }
  if (n) taskNames.set(key, n);
  return n;
}
function fieldOff(klass, name) { // instance field offset (objects have no sync word: the 4-byte vtable is counted), -1 if none
  const key = klass + ':' + name;
  if (fieldOffs.has(key)) return fieldOffs.get(key);
  let off = -1;
  try {
    const f = fieldFromName(klass, fieldNameStr[name]); // on the sim thread, at the iteration boundary: the game is between calls
    if (!f.isNull() && (f.readPointer().add(4).readU16() & 0x10) === 0) off = f.add(8).readS32(); // 0x10 = static
  } catch (e) { /* keep -1 */ }
  fieldOffs.set(key, off);
  return off;
}
const shortName = n => n.slice(n.lastIndexOf('.') + 1);
// the object's class; a wrapper (ScriptProxy.mTarget, OneShotFunctionTask/FunctionTask.mFunction) names what it wraps:
// the target's class, or a delegate's method (Delegate.method_info -> MonoReflectionMethod +4 = MonoMethod)
function objName(obj, klass) {
  const base = klassName(klass);
  if (!base) return null;
  for (const fname of ['mTarget', 'mFunction']) {
    const off = fieldOff(klass, fname);
    if (off <= 0) continue;
    try {
      const inner = obj.add(off).readPointer();
      if (inner.isNull()) break;
      const ik = inner.readPointer().readPointer(), mi = fieldOff(ik, 'method_info');
      let iname = null;
      if (mi > 0) { const r = inner.add(mi).readPointer(); if (!r.isNull()) iname = methodName(r.add(4).readPointer()); }
      iname = iname ?? klassName(ik);
      if (iname) return `${shortName(base)}>${iname}`;
    } catch (e) { /* inner object unreadable: the wrapper's own name */ }
    break;
  }
  return base;
}
const chain = ps => ps.map(p => p.isNull() ? null : methodName(p) ?? '?').filter(x => x).join(' < ');
// at the iteration boundary on the sim thread: the kept runs as [{stage, name, ms, from, to}], slowest first, and runs per stage
function readTasks() {
  const T = tasks, n = Math.min(NTOP, T.st.add(12).readU32()), size = T.st.add(24).readU32(), out = [];
  for (let i = 0; i < n; i++) {
    const s = T.top.add(i * size), ms = Number(s.readS64()) / ticksPerMs, kind = s.add(12).readU32();
    const obj = s.add(16).readPointer(), method = s.add(24).readPointer();
    let klass = s.add(20).readPointer(), name = null;
    try {
      if (!obj.isNull()) {
        const k = obj.readPointer().readPointer();
        if (klass.isNull()) klass = k; // invoke: taken here (the object is still in its seam's map)
        name = k.equals(klass) ? objName(obj, klass) : klassName(klass); // reused memory: class only
      }
    } catch (e) { /* object gone */ }
    if (!name && !klass.isNull()) name = klassName(klass);
    if (!method.isNull() && (kind === 2 || !name)) name = name ? `${name}: ${shortName(methodName(method) ?? '?')}` : methodName(method);
    const r = { stage: TASK_STAGES[s.add(8).readU32()] ?? '?', name: name ?? '?', ms: round(ms) };
    const from = chain([0, 1, 2].map(j => s.add(28 + 4 * j).readPointer())), to = chain([0, 1, 2].map(j => s.add(40 + 4 * j).readPointer()));
    if (from) r.from = from;
    if (to) r.to = to;
    const fromMore = chain([0, 1, 2].map(j => s.add(52 + 4 * j).readPointer())), toMore = chain([0, 1, 2].map(j => s.add(64 + 4 * j).readPointer()));
    if (fromMore) r.fromMore = fromMore;
    if (toMore) r.toMore = toMore;
    // a fresh task that ended in one run has no saved frames: its entry method names it (docs/247 rank 5)
    if (kind === 1 && !from && !to && !method.isNull()) r.entry = methodName(method) ?? undefined;
    out.push(r);
  }
  out.sort((a, b) => b.ms - a.ms);
  const runs = {};
  for (let i = 1; i < 5; i++) { const c = T.st.add(32 + 4 * i).readU32(); if (c) runs[TASK_STAGES[i]] = c; }
  return { tasks: out, taskRuns: runs, taskDropped: T.st.add(16).readU32() };
}
function resetTasks() { tasks.st.add(8).writeByteArray(TASK_ZERO.slice(0, 12)); tasks.st.add(32).writeByteArray(TASK_ZERO.slice(0, 20)); }
// seam hooks call this on the sim thread: 0 when the seam returns
function taskStage(id) { if (tasks && Process.getCurrentThreadId() === simThread) tasks.st.add(4).writeU32(id); }
function installTasks() {
  if (!cfg.tasks) { console.log('[ts3prof] task attribution off (tasks: false)'); return; }
  try {
    const at = Object.fromEntries(Object.keys(TASK_HOOKS).map(name => { HOOKS[name] = HOOKS[name] ?? TASK_HOOKS[name]; return [name, addr(name)]; }));
    const st = Memory.alloc(64), minTicks = Memory.alloc(8), top = Memory.alloc(NTOP * 96), yieldTo = Memory.alloc(32);
    minTicks.writeS64(Math.round(cfg.taskMinMs * ticksPerMs));
    const cm = new CModule(TASK_C, { QueryPerformanceCounter: k32.getExportByName('QueryPerformanceCounter'), st, minTicks, top, yieldTo });
    st.add(24).writeU32(new NativeFunction(cm.slotSize, 'int', [])());
    fieldFromName = new NativeFunction(ptr(cfg.fieldFromName ?? FIELD_FROM_NAME), 'pointer', ['pointer', 'pointer']);
    for (const f of ['mTarget', 'mFunction', 'method_info']) fieldNameStr[f] = Memory.allocUtf8String(f);
    const listeners = [Interceptor.attach(at.taskRun, { onEnter: cm.onEnterRun, onLeave: cm.onLeaveRun }),
      Interceptor.attach(at.scriptInvoke, { onEnter: cm.onEnterInvoke, onLeave: cm.onLeaveInvoke }),
      Interceptor.attach(at.yieldSave, { onEnter: cm.onEnterYield, onLeave: cm.onLeaveYield })];
    tasks = { cm, st, top, yieldTo, minTicks, listeners };
    console.log(`[ts3prof] task attribution: runs >= ${cfg.taskMinMs} ms named in hitch lines (CModule on 0xD7F000, 0xEA1090, 0xE53620)`);
  } catch (e) {
    console.log(`[ts3prof] task attribution off: ${e.message}`); // costs only the tasks field, never the profiler
  }
}
let simThread = null;
let iter = blankIter();
let iterStart = 0;
let sec = blankSecond();
let secStart = 0;
let inTryToCollect = false;
const entryStack = []; // script entry point for the current route call (sim thread)

let csv = null, jsonl = null, freezes = null;

function openOutputs() {
  const base = cfg.out || `ts3prof-${new Date().toISOString().replace(/[:.]/g, '-')}`;
  csv = new File(`${base}-seconds.csv`, 'w');
  jsonl = new File(`${base}-hitches.jsonl`, 'w');
  freezes = new File(`${base}-freezes.jsonl`, 'w');
  csv.write(Object.keys(blankSecond()).concat(['t']).join(',') + '\n');
  csv.flush();
  console.log(`[ts3prof] writing ${base}-seconds.csv and ${base}-hitches.jsonl (hitch >= ${cfg.hitchMs} ms)`);
}

function round(x) { return Math.round(x * 100) / 100; }

function endIteration(t) {
  hitchAttrFrame();
  lockHoldersFrame();
  if (iterStart === 0) { iterStart = t; secStart = t; iter = blankIter(); if (tasks) resetTasks(); return; }
  const wall = t - iterStart;
  sec.iters++;
  sec.maxIterMs = Math.max(sec.maxIterMs, wall);
  for (const k of ['script', 'gcMark', 'gcMarks', 'gcAborted', 'gcForced', 'gcForcedMs', 'fin', 'finRun',
                   'route', 'routeSearch', 'routes', 'routeFails', 'taskObjects', 'oneShot', 'threaded', ...EXTRA_STAGES,
                   'rtLock', 'rtOtherLock', 'rtJob', 'rtPlanWait', 'rtWaits']) sec[k] += iter[k];
  sec.maxSteps = Math.max(sec.maxSteps, iter.steps);
  if (wall >= cfg.hitchMs) {
    sec.hitches++;
    const rec = { t: round(t), wallMs: round(wall), n: iterNo };
    for (const [k, v] of Object.entries(iter)) rec[k] = typeof v === 'number' ? round(v) : v;
    if (tasks) Object.assign(rec, readTasks());
    jsonl.write(JSON.stringify(rec) + '\n');
    jsonl.flush();
  }
  if (tasks) resetTasks();
  if (t - secStart >= 1000) flushSecond(t);
  iter = blankIter();
  iterStart = t;
  iterNo++; itersSincePub++;
}

// on the sim thread at FrameDb_PublishFrame's return; n = the iteration it closes, iters = iteration boundaries inside the gap
// (n - iters .. n are the iterations it overlaps; their hitches.jsonl lines carry the same n)
function onPublish(t) {
  if (iterStart === 0) return; // before the first iteration boundary: no n yet
  if (lastPub) {
    const gap = t - lastPub;
    sec.publishes++;
    sec.maxPubGapMs = Math.max(sec.maxPubGapMs, gap);
    if (gap >= cfg.hitchMs) {
      freezes.write(JSON.stringify({ t0: round(lastPub), t1: round(t), gapMs: round(gap), prevGapMs: round(prevPubGap), n: iterNo, iters: itersSincePub }) + '\n');
      freezes.flush();
    }
    prevPubGap = gap;
  }
  lastPub = t;
  itersSincePub = 0;
}

function flushSecond(t) {
  lotStreamSecond();
  const row = Object.values(sec).map(v => round(v)).concat([round(t)]);
  csv.write(row.join(',') + '\n');
  csv.flush();
  if (sec.hitches > 0 || sec.maxIterMs > cfg.hitchMs)
    console.log(`[ts3prof] ${sec.iters} it/s, max ${round(sec.maxIterMs)} ms, hitches ${sec.hitches}, ` +
      `routes ${sec.routes} (${sec.routeFails} failed, ${round(sec.route)} ms), gc ${round(sec.gcMark)} ms ` +
      `(${sec.gcAborted} aborted, ${sec.gcForced} forced ${round(sec.gcForcedMs)} ms), fin ${round(sec.fin)} ms, ` +
      `stages tick ${round(sec.script)} task ${round(sec.taskObjects)} (max ${round(sec.maxTaskObjectsMs)}) ` +
      `oneShot ${round(sec.oneShot)} threaded ${round(sec.threaded)} anim ${round(sec.animSacs)}+${round(sec.animPose)} ` +
      `locomotion ${round(sec.locomotion)} publish ${round(sec.publish)} ms`);
  sec = blankSecond();
  secStart = t;
}

// --- hooks ---
function addr(name) {
  const [a, expect] = HOOKS[name];
  const p = ptr(a);
  const got = Array.from(new Uint8Array(p.readByteArray(8))).map(b => b.toString(16).padStart(2, '0')).join(' ');
  if (got.startsWith('e9')) { // detoured by a known plugin: time its wrapper instead
    const target = p.add(5).add(p.add(1).readS32());
    // ts3native's route detour, or S3PP's SimulatorStageProfiler (needed by its MonoTypeEHashProbe, so it stays on): both wrap
    // the original with the same arguments and result, so timing the wrapper times the stage
    if (['ts3native.asi', 'Sims3Performance.asi'].includes(Process.findModuleByAddress(target)?.name)) return target;
    // ts3native [MonoThread] P3 (docs/30 §14, GcLock=1): the stub lives in its own VirtualAlloc page and starts with
    // `cmp dword [gate], 0` (83 3D <gate>), the gate inside ts3native.asi; it runs the original with the same arguments
    // and result (locked or not), so timing the stub times the call. Before this, every GcLock=1 run was NOT PROFILING.
    if (target.readU8() === 0x83 && target.add(1).readU8() === 0x3d &&
        Process.findModuleByAddress(target.add(2).readPointer())?.name === 'ts3native.asi') return target;
  }
  if (got !== expect) throw new Error(`${name} @ ${p}: bytes ${got}, expected ${expect}. ` +
    (got.startsWith('e9') ? 'Detoured by another plugin (S3PP SimulatorStageProfiler? turn it off).' : 'Different TS3W.exe build?'));
  return p;
}

function timed(name, onDone, onEnterExtra) {
  Interceptor.attach(addr(name), {
    onEnter(args) { this.t0 = now(); if (onEnterExtra) onEnterExtra.call(this, args); },
    onLeave(retval) { onDone.call(this, now() - this.t0, retval); },
  });
}

function install() {
  if (cfg.taskOnly) { // dummy test only (tools/probe/stutter-tasks-test.py): boundary, seams and task attribution
    for (const name of ['gcTryToCollect', 'scriptTick', 'taskObjects', 'oneShot', 'threaded']) addr(name);
  } else {
    const mod = Process.getModuleByName('TS3W.exe');
    if (!mod.base.equals(ptr(0x400000))) throw new Error(`TS3W.exe loaded at ${mod.base}, expected 0x400000`);
    for (const name of Object.keys(HOOKS)) addr(name); // check every prologue before the first attach: all or nothing
  }
  // simulate is only verified, never hooked: it loops forever
  // stages: false (docs/168 §4, profiler cost control): boundary, catch-up steps and publish gaps only; no stage, GC, route or
  // task hooks, so hitch lines keep wallMs/steps/n and freezes.jsonl stays complete, but carry no stage split
  const stages = cfg.stages !== false || cfg.taskOnly;
  if (!stages) { cfg.tasks = false; console.log('[ts3prof] stage hooks off (stages: false): iterations, steps and publish gaps only'); }
  installTasks(); // before the boundary hook: it hands the sim thread id to the CModule

  // One GC_try_to_collect call per Simulate iteration: use its entry as the iteration boundary.
  Interceptor.attach(addr('gcTryToCollect'), {
    onEnter() {
      if (simThread === null) { simThread = Process.getCurrentThreadId(); if (tasks) tasks.st.writeU32(simThread); }
      if (Process.getCurrentThreadId() !== simThread) return;
      endIteration(now());
      inTryToCollect = true;
      if (clockOn && this.returnAddress.equals(CLOCK_RET)) { // esi = Simulate's host (callee-saved, still the caller's here)
        const host = this.context.esi;
        if (host.add(0xa74).readU8() !== 0) sec.simPaused = 1;
        sec.stepAcc = host.add(0xc14).readFloat();
      }
    },
    onLeave() { inTryToCollect = false; },
  });

  if (!cfg.taskOnly) Interceptor.attach(addr('catchUpSteps'), {
    onEnter(args) { if (Process.getCurrentThreadId() === simThread) iter.steps += args[0].toInt32(); },
  });
  if (stages) installStages();
  if (cfg.taskOnly) return;

  try { // after the all-or-nothing check: a mismatch here costs only the freeze log
    HOOKS.publishFrame = PUBLISH_FRAME;
    Interceptor.attach(addr('publishFrame'), { onLeave() { if (Process.getCurrentThreadId() === simThread) onPublish(now()); } });
    console.log('[ts3prof] publish gaps: FrameDb_PublishFrame 0x593600 timed on the sim thread (freezes.jsonl)');
  } catch (e) {
    console.log(`[ts3prof] publish gaps off (freezes.jsonl stays empty): ${e.message}`);
  }
  installClock();
  console.log('[ts3prof] hooks installed; the sim thread is detected on the first GC_try_to_collect');
}

function installStages() {
  timed('scriptTick', function (ms) {
    taskStage(0);
    if (Process.getCurrentThreadId() === simThread) iter.script += ms;
  }, () => taskStage(1));
  for (const stage of ['taskObjects', 'oneShot', 'threaded']) {
    timed(stage, function (ms) {
      taskStage(0);
      if (Process.getCurrentThreadId() !== simThread) return;
      iter[stage] += ms;
      if (stage === 'taskObjects') sec.maxTaskObjectsMs = Math.max(sec.maxTaskObjectsMs, ms);
    }, () => taskStage(TASK_STAGES.indexOf(stage)));
  }
  if (cfg.taskOnly) return;

  timed('gcStoppedMark', function (ms, retval) {
    if (Process.getCurrentThreadId() !== simThread) return;
    iter.gcMark += ms; iter.gcMarks++;
    sec.maxGcMarkMs = Math.max(sec.maxGcMarkMs, ms);
    if (retval.toInt32() === 0) iter.gcAborted++; // stop function fired: mark work thrown away
    if (!this.perFrame) { iter.gcForced++; iter.gcForcedMs += ms; } // collection triggered by an allocation
  }, function () { this.perFrame = inTryToCollect; });

  timed('gcInvokeFinalizers', function (ms, retval) {
    if (Process.getCurrentThreadId() !== simThread) return;
    iter.fin += ms; iter.finRun += retval.toInt32();
  });

  for (const stage of EXTRA_STAGES) {
    timed(stage, function (ms) {
      if (Process.getCurrentThreadId() === simThread) iter[stage] += ms;
    });
  }

  for (const name of ROUTE_ENTRIES) {
    Interceptor.attach(addr(name), {
      onEnter() { entryStack.push(name); },
      onLeave() { entryStack.pop(); },
    });
  }

  timed('routeSearch', function (ms) {
    if (Process.getCurrentThreadId() === simThread) iter.routeSearch += ms;
  });

  timed('routeOuter', function (ms, retval) {
    if (Process.getCurrentThreadId() !== simThread) { sec.routeOtherThreads++; return; }
    const ok = (retval.toInt32() & 0xff) !== 0;
    const entry = entryStack.length ? entryStack[entryStack.length - 1] : 'other';
    iter.route += ms; iter.routes++;
    if (!ok) iter.routeFails++;
    sec.maxRouteMs = Math.max(sec.maxRouteMs, ms);
    const e = iter.byEntry[entry] || (iter.byEntry[entry] = { n: 0, fail: 0, ms: 0 });
    e.n++; if (!ok) e.fail++; e.ms = round(e.ms + ms);
  });
}

// Observe only, no hook of its own: a mismatch leaves simPaused 0 and stepAcc -1 instead of stopping the whole profiler.
function installClock() {
  const hex = (p, n) => Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join(' ');
  const bad = CLOCK_CHECKS.map(([a, expect]) => [a, hex(ptr(a), expect.split(' ').length), expect]).filter(([, got, expect]) => got !== expect);
  if (bad.length) {
    console.log(`[ts3prof] P-clock off: ${bad.map(([a, got, expect]) => `0x${a.toString(16)} ${got} (expected ${expect})`).join('; ')}`);
    return;
  }
  clockOn = true;
  console.log('[ts3prof] P-clock: simPaused and stepAcc read from Simulate\'s host (esi) at each GC_try_to_collect call from 0xD819AA');
}

let started = false;
function start(parameters) {
  if (started) return;
  started = true;
  cfg = Object.assign(cfg, parameters || {});
  // test-only (dummy harness): {name: [address, expected bytes]} replaces HOOKS / TASK_HOOKS entries; never set for the game
  for (const [k, [a, b]] of Object.entries(cfg.addrs || {})) HOOKS[k] = [Number(a), b];
  openOutputs();
  watchStop();
  try {
    install();
  } catch (e) {
    console.error(`[ts3prof] NOT PROFILING: ${e.message}`); // a wrong build must never be hooked half-way
  }
}
setTimeout(() => start({}), 1000); // fallback when the host does not call init

let closed = false;
function closeAll() {
  if (closed) return;
  closed = true;
  // C hooks first: a sim thread inside a task run then skips their onLeave (Gum drops a detached listener's leave callback)
  if (tasks) { for (const l of tasks.listeners) l.detach(); Interceptor.flush(); }
  if (csv) { flushSecond(now()); csv.close(); jsonl.close(); freezes.close(); }
}
// -P stopFile (profile-world.ts early end, docs/168 §6): once the file exists, every hook off, then the dispose work; the
// harness closes the repl on the line below. One poll a second on the agent's JS thread, only when stopFile is set.
function watchStop() {
  if (!cfg.stopFile) return;
  const iv = setInterval(() => {
    try { File.readAllText(cfg.stopFile); } catch (e) { return; } // not there yet
    clearInterval(iv);
    if (tasks) { for (const l of tasks.listeners) l.detach(); tasks.listeners = []; } // C hooks first, as in closeAll
    Interceptor.detachAll(); Interceptor.flush();
    closeAll();
    console.log('[ts3prof] stopped early (stop file): hooks off, files closed');
  }, 1000);
}

rpc.exports = {
  init(stage, parameters) { start(parameters); },
  dispose() { closeAll(); },
};
