// First-time Mono runtime work during play (docs/214): read-only Frida script for TS3W.exe (Steam 1.67). Logs every
// entry into the eight cold paths a second script thread must not hit (docs/206 §8 X11, docs/58 §2):
//   transform    0xE643B0  IL -> mint of a method (arg 1 = RuntimeMethod, +8 = MonoMethod) [verified prologue]
//   vtable       0xEA1E30  mono_class_create_runtime_vtable, slow path (class in EBX: register argument) [verified prologue]
//   cctor        0xEA3370  mono_runtime_class_init, counted only past its fast exit (vtable+0x18 bit 1 clear; vtable+0 = class)
//   thunk        0xE51400  tramp_create_thunk, first native call of a signature (named by the frames above it)
//   image        0xD6FCB0  EA image loader on an image-cache miss (named by the frames above it)
//   classinit    0xE6EE20  mono_class_init past its fast exit (class+0x14 bit 0 clear; arg 1 = class) [verified prologue]
//   icall        0xE82680  mono_lookup_internal_call (arg 1 = MonoMethod) [verified prologue]
//   finalizer    0xE78F60  object finalizer registration on allocation (arg 1 = object, [object] = vtable, +0 class)
// Per event: kind, name, thread, ms since attach, count, and the interpreter frames of that thread at the first hit
// (ThreadContext from the TLS slot at [0x11F4160], +8 current frame, frame +0 parent, +4 RuntimeMethod; docs/130 §2).
//
//   python -m frida_tools.repl -n TS3W.exe -l tools/frida/cold-paths.js -P '{"out":"probe-out/profile/x","minutes":30}' -q -t 2000
// Output: <out>-coldpaths.json (read by tools/autonomy-cold-paths.ts). Progress line every 60 s on stdout.
//
// Writes no game memory and calls no game function. Hook prologues are checked first: a mismatch refuses that hook,
// except a jmp rel32 into executable memory (ts3native [Verify] or another Frida session on the same site), which is
// chained and listed. Every read is guarded; frame walks and the event table are bounded.
'use strict';

const cfg = { out: null, minutes: 30, frames: 40 };
const HOOKS = {
  transform: [0xe643b0, '55 8b ec 83 ec 78 53 8b 5d 08'],
  vtable: [0xea1e30, '83 ec 30 56 57 8b 7c 24 3c'],
  cctor: [0xea3370, '83 ec 18 53 8b 5c 24 20 f6 43 18 02'],
  thunk: [0xe51400, '55 8b ec 83 ec 14 53 56 8b 75 08'],
  image: [0xd6fcb0, '81 ec 24 02 00 00 56 57'],
  classinit: [0xe6ee20, '83 ec 34 53 55 8b 6c 24 40'],
  icall: [0xe82680, '81 ec 08 08 00 00 53 55 8b ac 24 14 08 00 00'],
  finalizer: [0xe78f60, '56 8b 74 24 08 57 bf 20 8e e7 00'],
};
const TLS_INDEX = ptr('0x11f4160');
const MAX_EVENTS = 200000;
const t0 = Date.now();
const events = new Map(); // `${kind}|${name}` -> record
const perMinute = {}; // kind -> [count per minute since attach]
const hooked = [], refused = [], chained = [];
let dropped = 0, readErrors = 0;

const ntQueryThread = new NativeFunction(Process.getModuleByName('ntdll.dll').getExportByName('NtQueryInformationThread'), 'int', ['pointer', 'int', 'pointer', 'uint', 'pointer']);
const safeRead = p => { try { return p.readPointer(); } catch (e) { readErrors++; return null; } };
const safeU32 = p => { try { return p.readU32(); } catch (e) { readErrors++; return null; } };
const cstr = p => { // metadata names end at the first non-printable byte
  if (!p || p.isNull()) return '';
  try { const b = new Uint8Array(p.readByteArray(160)); let e = 0; while (e < b.length && b[e] >= 0x20 && b[e] < 0x7f) e++; return String.fromCharCode(...b.subarray(0, e)); }
  catch (e) { readErrors++; return ''; }
};
const names = new Map();
function className(k, d = 0) { // MonoClass name +0x34, namespace +0x38, nested_in +0x28 (as tools/probe/agent.js vcWalker)
  if (!k || k.isNull()) return '?';
  const key = 'c' + k;
  if (names.has(key)) return names.get(key);
  const n = cstr(safeRead(k.add(0x34))) || '?', nest = safeRead(k.add(0x28)), ns = cstr(safeRead(k.add(0x38)));
  const r = nest && !nest.isNull() && d < 8 ? `${className(nest, d + 1)}+${n}` : ns ? `${ns}.${n}` : n;
  names.set(key, r);
  return r;
}
function methodKey(m) { // "Namespace.Outer+Inner::Name", the docs/78 index key without the parameter list
  if (!m || m.isNull()) return '?';
  const key = 'm' + m;
  let r = names.get(key);
  if (r === undefined) { r = `${className(safeRead(m.add(8)))}::${cstr(safeRead(m.add(0x18))) || '?'}`; names.set(key, r); }
  return r;
}
const tebs = new Map();
function ownContext() { // ThreadContext of the calling thread, or null (not an interpreter thread)
  const tid = Process.getCurrentThreadId();
  let teb = tebs.get(tid);
  if (teb === undefined) {
    const tbi = Memory.alloc(28); // THREAD_BASIC_INFORMATION: ExitStatus, TebBaseAddress, ...
    teb = ntQueryThread(ptr(-2), 0, tbi, 28, NULL) === 0 ? tbi.add(4).readPointer() : null; // -2 = current thread
    tebs.set(tid, teb);
  }
  if (!teb) return null;
  const idx = safeU32(TLS_INDEX);
  if (idx === null) return null;
  const slot = idx < 64 ? teb.add(0xe10 + idx * 4) : safeRead(teb.add(0xf94))?.add((idx - 64) * 4);
  const ctx = slot ? safeRead(slot) : null;
  return ctx && !ctx.isNull() ? ctx : null;
}
function frames(ctx) {
  const out = [];
  let f = ctx ? safeRead(ctx.add(8)) : null;
  for (let i = 0; i < cfg.frames && f && !f.isNull(); i++) {
    const rm = safeRead(f.add(4)), m = rm && !rm.isNull() ? safeRead(rm.add(8)) : null;
    if (m && !m.isNull()) addImageOf(safeRead(m.add(8)));
    out.push(m && !m.isNull() ? methodKey(m) : '?');
    f = safeRead(f);
  }
  return out;
}
// Images seen through events and frames (MonoClass +0 = image); the end snapshot walks their class caches.
const images = new Set();
const SEEDS = { corlibImage: ptr('0x1220e40'), simulatorClass: ptr('0x1220f34') }; // as tools/probe/agent.js VC
function addImageOf(klass) { const img = klass && !klass.isNull() ? safeRead(klass) : null; if (img && !img.isNull()) images.add(img.toString()); }
let domainId = 0; // root domain; replaced by [domain+0x58] once 0xEA1E30 shows the domain (stack argument 1)
// End snapshot: every class in those images' class caches (image+0x298: buckets +8, size +0xC; slot +4 class, +8 next):
// vtable present ([class+0xB8] runtime_info: u16 max_domain, vtables from +4, as mono_class_vtable 0xEA2AD0), class init
// done (vtable+0x18 bit 1, as 0xEA3370), and its methods (+0x70 count, +0x94 array) whose RuntimeMethod (MonoMethod+0x14)
// is transformed (+0x44 bit 0x100, as agent.js vcWalker).
function snapshot() {
  const t = Date.now(), classes = [], transformed = [];
  let vtMismatch = 0;
  const corlib = safeRead(SEEDS.corlibImage); if (corlib && !corlib.isNull()) images.add(corlib.toString());
  addImageOf(safeRead(SEEDS.simulatorClass));
  for (const s of images) {
    const img = ptr(s), h = safeRead(img.add(0x298));
    if (!h || h.isNull()) continue;
    const buckets = safeRead(h.add(8)), size = safeU32(h.add(0xc));
    if (!buckets || buckets.isNull() || size === null || size > (1 << 20)) continue;
    for (let b = 0; b < size; b++) {
      let slot = safeRead(buckets.add(4 * b));
      for (let guard = 0; slot && !slot.isNull() && guard < 1000; guard++, slot = safeRead(slot.add(8))) {
        const k = safeRead(slot.add(4));
        if (!k || k.isNull()) continue;
        const ri = safeRead(k.add(0xb8));
        let vt = null;
        if (ri && !ri.isNull()) { const max = ri.readU16(); if (max >= domainId) vt = safeRead(ri.add(4 + 4 * domainId)); }
        if (vt && !vt.isNull() && !safeRead(vt)?.equals(k)) { vtMismatch++; vt = null; }
        const fl = vt && !vt.isNull() ? safeU32(vt.add(0x18)) : null;
        const name = className(k);
        classes.push([name, vt && !vt.isNull() ? 1 : 0, fl !== null && (fl & 2) ? 1 : 0]);
        const n = safeU32(k.add(0x70)), arr = safeRead(k.add(0x94));
        if (!arr || arr.isNull() || n === null || n > 20000) continue;
        for (let i = 0; i < n; i++) {
          const m = safeRead(arr.add(4 * i)), rm = m && !m.isNull() ? safeRead(m.add(0x14)) : null;
          const rf = rm && !rm.isNull() ? safeU32(rm.add(0x44)) : null;
          if (rf !== null && (rf & 0x100)) transformed.push(methodKey(m));
        }
      }
    }
  }
  return { ms: Date.now() - t, images: images.size, domainId, vtMismatch, classes, transformed };
}
function note(kind, name) {
  const ms = Date.now() - t0, min = Math.floor(ms / 60000);
  (perMinute[kind] = perMinute[kind] || [])[min] = (perMinute[kind][min] || 0) + 1;
  const key = `${kind}|${name}`;
  const e = events.get(key);
  if (e) { e.count++; e.lastMs = ms; return; }
  if (events.size >= MAX_EVENTS) { dropped++; return; }
  const ctx = ownContext();
  events.set(key, { kind, name, tid: Process.getCurrentThreadId(), interp: !!ctx, firstMs: ms, lastMs: ms, count: 1, stack: ctx ? frames(ctx) : [] });
}
const ON_ENTER = {
  transform(args) { const m = safeRead(args[0].add(8)); if (m && !m.isNull()) addImageOf(safeRead(m.add(8))); note('transform', methodKey(m)); },
  vtable(args) {
    const id = safeU32(args[0].add(0x58)); if (id !== null && id < 64) domainId = id;
    addImageOf(this.context.ebx); note('vtable', className(this.context.ebx));
  },
  cctor(args) { const vt = args[0], fl = safeU32(vt.add(0x18)); if (fl !== null && !(fl & 2)) { const k = safeRead(vt); addImageOf(k); note('cctor', className(k)); } },
  thunk() { const ctx = ownContext(), fr = ctx ? frames(ctx) : []; note('thunk', fr[0] || '?'); },
  image() { const ctx = ownContext(), fr = ctx ? frames(ctx) : []; note('image', fr[0] || '?'); },
  classinit(args) { const k = args[0], fl = k.isNull() ? null : safeU32(k.add(0x14)); if (fl !== null && !(fl & 1)) { addImageOf(k); note('classinit', className(k)); } },
  icall(args) { note('icall', methodKey(args[0])); },
  finalizer(args) { const vt = args[0].isNull() ? null : safeRead(args[0]); note('finalizer', className(vt && !vt.isNull() ? safeRead(vt) : null)); },
};
function prologueOk(at, pattern) {
  const want = pattern.split(' ').map(h => parseInt(h, 16));
  const got = new Uint8Array(at.readByteArray(want.length));
  if (want.every((b, i) => got[i] === b)) return 'ok';
  if (got[0] === 0xe9) { // jmp rel32 (another hook): chain when it lands in executable memory
    const dst = at.add(5 + at.add(1).readS32());
    const r = Process.findRangeByAddress(dst);
    if (r && r.protection.includes('x')) return 'chained';
  }
  return 'mismatch';
}
function install() {
  for (const [kind, [addr, pattern]] of Object.entries(HOOKS)) {
    const at = ptr(addr), st = prologueOk(at, pattern);
    if (st === 'mismatch') { refused.push(kind); continue; }
    if (st === 'chained') chained.push(kind);
    const fn = ON_ENTER[kind];
    Interceptor.attach(at, { onEnter(args) { try { fn.call(this, args); } catch (e) { readErrors++; } } });
    hooked.push(kind);
  }
}
function result(snap) {
  return { version: 1, startedAt: new Date(t0).toISOString(), ms: Date.now() - t0, minutes: cfg.minutes, hooked, refused, chained,
    dropped, readErrors, perMinute, events: [...events.values()], snapshot: snap || null };
}
let done = false;
function finish() {
  if (done) return; done = true;
  Interceptor.detachAll();
  let snap = null;
  try { snap = snapshot(); } catch (e) { snap = { error: String(e) }; }
  if (cfg.out) { const f = new File(`${cfg.out}-coldpaths.json`, 'w'); f.write(JSON.stringify(result(snap))); f.close(); }
  console.log(`cold-paths: done, ${events.size} distinct events, written ${cfg.out}-coldpaths.json`);
}
let started = false;
function start(parameters) {
  if (started) return; started = true;
  Object.assign(cfg, parameters || {});
  install();
  console.log(`cold-paths: hooked ${hooked.join(',') || 'none'}${refused.length ? `; REFUSED ${refused.join(',')}` : ''}${chained.length ? `; chained ${chained.join(',')}` : ''}; ${cfg.minutes} min`);
  const tick = setInterval(() => {
    const kinds = {}; for (const e of events.values()) kinds[e.kind] = (kinds[e.kind] || 0) + e.count;
    console.log(`cold-paths: ${Math.round((Date.now() - t0) / 1000)} s, ${JSON.stringify(kinds)}`);
  }, 60000);
  setTimeout(() => { clearInterval(tick); finish(); }, cfg.minutes * 60000);
}
setTimeout(() => start({}), 1000); // fallback when the host does not call init

rpc.exports = {
  init(stage, parameters) { start(parameters); },
  dump() { return result(); },
  dispose() { finish(); },
};
