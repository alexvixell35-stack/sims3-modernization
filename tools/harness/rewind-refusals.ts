// Rewind refusal tally (docs/270 §33): every reason a rewind or snapshot was refused or failed, one jsonl row per reason with its
// kind and the owner (start module of the thread, module, section or the bridge), so a new kind shows up at once.
//   recordRefusals(file, ev)  queue.ts after a snapshot/rewind op with code != 0: appends the rows, returns the reasons whose
//                             kind+owner pair was not in the file before (queue.ts logs them as "new refusal kind")
//   node tools/harness/rewind-refusals.ts tally [--file f]                  counts by kind and owner
//   node tools/harness/rewind-refusals.ts backfill [--from dir] [--file f] [--write]
//                             past refusals from chain logs (probe-out/queue/chain-*.out: rewind-smoke "refuse:" lines and
//                             queue "rewind refused -> fresh (...)" / "why in full:" lines); prints them, --write appends
//   node tools/harness/rewind-refusals.ts --self-check
import assert from 'node:assert/strict';
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

export const REFUSALS = resolve(import.meta.dirname, '..', '..', 'probe-out', 'rewind-refusals.jsonl');

// kind, matcher, where the guard is described; first match wins (narrow before wide)
const KINDS: [string, RegExp, string][] = [
  ['pool-worker-made-after', /is a pool worker made after the snapshot/, '§16.3 --reap-workers'],
  ['thread-made-after', /was made after the snapshot/, '§16.3'],
  ['thread-ended', /exited after the snapshot/, '§25 pool hold, §32 keepalive/settle'],
  ['wait-differs', /at the snapshot it was in \w+; differs:/, '§17'],
  ['wait-changed', /at the snapshot it was in |^thread \d+ is in Nt\w+$/, '§16.2 (the bare form: cut at 200 characters)'],
  ['pool-work-item', /thread-pool worker inside a work item/, '§18, §22'],
  ['upper-vector', /upper vector registers/, '§14.4'],
  ['write-watch', /write.watch|GetWriteWatch/i, '§15'],
  ['busy-syscall', /is inside Nt\w+/, '§14, §18'],
  ['shared-view', /writable view (shared|.* looks shared) with another process/, '§20, §23'],
  ['module-guard', /module \S+ is (still )?loaded/, '§8, §24, §36 (gone wait)'],
  ['handle-replaced', /was closed or replaced after the snapshot/, '§35, §36'],
  ['gui-thread-handle', /made after the snapshot is protected from close/, '§36 (a thread made after the snapshot that called win32k)'],
  ['gui-objects', /window-system objects changed/, '§36'],
  ['sound', /sound (device|module)/, '§23'],
  ['no-park', /did not park|did not come back to its idle wait/, '§21, §27'],
  ['no-coop', /no cooperation block/, '§9.1'],
  ['bridge', /bridge (server refused|client did not answer)|bridge server state is unknown/, '§28, §31'],
  ['pause', /^pause: /, 'queue-rewind'],
  ['probe-attach', /^probe attach: /, '§24'],
  ['game-no-answer', /game does not answer after/, 'queue-rewind'],
  ['hash-differs', /memory hash differs/, '§8.7'],
  ['switches-differ', /switches differ from the load/, '§30'],
  ['no-snapshot', /^no snapshot/, 'tool'],
  ['tool-access', /cannot (read region|suspend)/, '§8'],
];

export type Refusal = { kind: string; owner: string; doc: string };
export function classify(why: string): Refusal {
  const w = why.replace(/^park: /, '');
  const k = KINDS.find(([, re]) => re.test(w));
  const m = w.match(/start ([\w.-]+\.(?:dll|exe|asi|drv))\+[0-9a-f]+/i) ?? w.match(/sound module ([\w.-]+)/) ?? w.match(/module ([\w.-]+) is (?:still )?loaded/);
  const a = w.match(/start ([0-9a-f]{6,8})\b/i), s = w.match(/section (?:\\(?:[^\\,]*\\)*([A-Za-z]+)|(unnamed))/);
  const owner = m ? m[1].toLowerCase() : a ? `addr:${a[1].toLowerCase()}` : s ? `section:${s[1] ?? s[2]}` : /bridge/.test(w) ? 'bridge'
    : /route worker|did not park/.test(w) ? 'ts3native.asi' : '?';
  return k ? { kind: k[0], owner, doc: k[2] } : { kind: 'new', owner, doc: '' };
}

export type Ev = { queue?: string; job?: string; op: string; code: number; why: string[]; pid?: number; source?: string; time?: string };
const pairs = (file: string) => new Set(existsSync(file) ? readFileSync(file, 'utf8').split('\n').filter(Boolean).map(l => {
  try { const r = JSON.parse(l); return `${r.kind}|${r.owner}`; } catch { return ''; }
}) : []);
// appends one row per reason; returns "kind owner: reason" for each pair not seen in the file before (also kind 'new')
export function recordRefusals(file: string, ev: Ev): string[] {
  const seen = pairs(file), fresh: string[] = [];
  mkdirSync(dirname(file), { recursive: true });
  for (const why of ev.why) {
    const c = classify(why), key = `${c.kind}|${c.owner}`;
    if (!seen.has(key) || c.kind === 'new') { seen.add(key); fresh.push(`${c.kind} ${c.owner}: ${why.slice(0, 200)}`); }
    appendFileSync(file, JSON.stringify({ time: ev.time ?? new Date().toISOString(), queue: ev.queue ?? null, job: ev.job ?? null, op: ev.op,
      code: ev.code, pid: ev.pid ?? null, source: ev.source ?? 'queue', ...c, why }) + '\n');
  }
  return fresh;
}

export function tally(file: string): string {
  const rows = existsSync(file) ? readFileSync(file, 'utf8').split('\n').filter(Boolean).map(l => JSON.parse(l)) : [];
  const by = new Map<string, number>();
  for (const r of rows) by.set(`${r.kind}\t${r.owner}`, (by.get(`${r.kind}\t${r.owner}`) ?? 0) + 1);
  return [`${rows.length} refusal reasons in ${file}`, ...[...by].sort((a, b) => b[1] - a[1]).map(([k, n]) => `${String(n).padStart(5)}  ${k}`)].join('\n');
}

// chain logs -> events; a reason counts once per chain (the -1/-2/watch copies of a chain log repeat it)
const REFUSE = /refuse: (.+)$/, QREF = /^(\d\d:\d\d:\d\d) queue: ([\w.-]+): start: rewind (refused|failed) -> fresh \((.*)\)$/,
  FULL = /^(\d\d:\d\d:\d\d) queue: ([\w.-]+): rewind: why in full: (.*)$/;
export function backfill(dir: string): Ev[] {
  const out: Ev[] = [], seen = new Set<string>();
  for (const f of readdirSync(dir).filter(f => /^chain-.*\.out$/.test(f) && !/build|restore/.test(f)).sort()) {
    const chain = f.replace(/\.out$/, '').replace(/-\d+(-.*)?$|-watch$/, '');
    for (const l of readFileSync(join(dir, f), 'utf8').split(/\r?\n/)) {
      const r = l.match(REFUSE), q = l.match(QREF), u = l.match(FULL);
      const ev: Ev | null = u ? { job: u[2], op: 'rewind', code: 2, why: u[3].split('; ') }
        : q ? { job: q[2], op: 'rewind', code: q[3] === 'refused' ? 2 : 3, why: q[4].split('; ').filter(w => !/^at the snapshot it was in \w*$|^its reason is in/.test(w)) }
        : r ? { op: 'rewind', code: 2, why: [r[1]] } : null;
      if (!ev) continue;
      ev.why = ev.why.filter(w => { const k = `${chain}|${w.replace(/thread \d+/, 'thread')}`; return !seen.has(k) && !!seen.add(k); });
      if (ev.why.length) out.push({ ...ev, queue: chain, source: `backfill ${f}` });
    }
  }
  return out;
}

function selfCheck() {
  // real reasons from chain logs (chain-correct-1116b, chain-rewind-rerun*-1115, the toy builds) and the other branch's owner form
  const C = (w: string) => { const c = classify(w); return `${c.kind} ${c.owner}`; };
  assert.equal(C('thread 69272 (start 76dd3bf0) exited after the snapshot'), 'thread-ended addr:76dd3bf0');
  assert.equal(C('thread 69272 (start combase.dll+123bf0, COM RPC cache worker (ends after 30 s idle)) exited after the snapshot'), 'thread-ended combase.dll');
  assert.equal(C('thread 54088 is a pool worker made after the snapshot (--reap-workers ends idle ones)'), 'pool-worker-made-after ?');
  assert.equal(C('thread 50552 (start 6935dbc0, in NtWaitForSingleObject) was made after the snapshot'), 'thread-made-after addr:6935dbc0');
  assert.equal(C('thread 6120 is in NtWaitForSingleObject; at the snapshot it was in NtWaitForSingleObject; differs: esi 271->290'), 'wait-differs ?');
  assert.equal(C('thread 65788 is in NtWaitForMultipleObjects; at the snapshot it was in NtWaitForMultipleObjects; differs: esp 2a6fbf4->2a6e5e8 ebp 2a6fd80->2a6e774 arg2 2a6fdbc->2a6fdb4; start rewind-toy.exe+13b7, returns to kernelbase.dll+160f3f (snapshot kernelbase.dll+160f3f)'), 'wait-differs rewind-toy.exe');
  assert.equal(C('thread 61324 is in NtWaitForSingleObject; at the snapshot it was in NtWaitForSingleObject'), 'wait-changed ?');
  assert.equal(C('thread 16004 is inside NtFsControlFile'), 'busy-syscall ?');
  assert.equal(C('thread 71284 is in NtWaitForSingleObject'), 'wait-changed ?', 'the queue line cuts the reason');
  assert.equal(C('thread 63452 is inside NtGetWriteWatch'), 'write-watch ?');
  assert.equal(C('allocation 0000000017A60000 uses write watch (its dirty bits cannot be put back)'), 'write-watch ?');
  assert.equal(C('thread 41756 has live upper vector registers'), 'upper-vector ?');
  assert.equal(C('thread 63096 is a thread-pool worker inside a work item (the stack modules below name its callback) (start ntdll.dll+54e70, syscall 60034'), 'pool-work-item ntdll.dll');
  assert.equal(C('a writable view shared with another process changed since the snapshot and is not put back: 0000000003630000 (+2c000, section \\Sessions\\1\\BaseNamedObjects\\QueryResults_bacd2d83-2888-4040-86cc-3f54fa57200e, 4 handles, 3 ours'), 'shared-view section:QueryResults');
  assert.equal(C('writable view 0000000003630000 (+1000) looks shared with another process (section unnamed, 12 handles, 3 ours)'), 'shared-view section:unnamed');
  assert.equal(C('the ts3native route worker did not come back to its idle wait within 5000 ms ([Rewind] Park)'), 'no-park ts3native.asi');
  assert.equal(C('park: the target did not park within 5000 ms (it passed its park point 3 times)'), 'no-park ts3native.asi');
  assert.equal(C('the bridge server refused op 3 (result 80004005; its reason is in the bridge server log, [Rewind] lines)'), 'bridge bridge');
  assert.equal(C('module frida-agent.dll is loaded (detach the probe first)'), 'module-guard frida-agent.dll');
  assert.equal(C('thread 7 belongs to the sound module dsound.dll'), 'sound dsound.dll');
  assert.equal(C('pause: no answer'), 'pause ?');
  // chain-1117a bp-rewind-pilot-c 21:08:26 (docs/270 §36) and the tool's gone/new-handle forms
  assert.equal(C("handle 7b8 (ALPC Port) was closed or replaced after the snapshot and would not come back as the snapshot's object: comes back as type 48 access 1f0000 (snapshot type 48 access 1f0001)"), 'handle-replaced ?');
  assert.equal(C('handle 654 made after the snapshot is protected from close (window-system state of a thread)'), 'gui-thread-handle ?');
  assert.equal(C('handle 26c (Event) made after the snapshot is protected from close (window-system state of a thread)'), 'gui-thread-handle ?');
  assert.equal(C('window-system objects changed (GDI 170 -> 194, USER 76 -> 86): they are not rewound'), 'gui-objects ?');
  assert.equal(C('module frida-agent.dll is still loaded 30 s after the probe detached: another Frida session is in the game (a kept frames session?)'), 'module-guard frida-agent.dll');
  assert.equal(C('handle 7a8 (target ended) was closed after the snapshot'), 'new ?', 'non-match: closed without "or replaced"');
  assert.equal(C('something nobody has seen'), 'new ?', 'non-match: an unknown reason is a new kind');
  assert.ok(!REFUSE.test('rewind-smoke: snapshot ok') && !QREF.test('11:10:56 queue: bp-x: start: rewind (4100 ms)') && !FULL.test('11:10:56 queue: bp-x: rewind: snapshot taken (900 ms)'), 'log non-matches');
  // record: rows appended, only new pairs returned; backfill from a fake chain dir, one reason per chain
  const d = join(tmpdir(), `rwref-${process.pid}`), f = join(d, 'r.jsonl');
  rmSync(d, { recursive: true, force: true }); mkdirSync(d);
  const why = ['thread 1 (start 76dd3bf0) exited after the snapshot', 'thread 2 (start 74497710) exited after the snapshot'];
  assert.equal(recordRefusals(f, { op: 'rewind', code: 2, why, job: 'a' }).length, 2);
  assert.deepEqual(recordRefusals(f, { op: 'rewind', code: 2, why: [why[0], 'odd'], job: 'b' }), ['new ?: odd']);
  assert.match(tally(f), /^4 refusal reasons[\s\S]*\n {4}2 {2}thread-ended\taddr:76dd3bf0/);
  const line = '11:10:56 queue: bp-seen-dry-crowd: start: rewind refused -> fresh (thread 69272 (start 76dd3bf0) exited after the snapshot; thread 71284 is in NtWaitForSingleObject; at the snapshot it was in NtWaitForSingleObj)';
  appendFileSync(join(d, 'chain-x.out'), `${line}\n${line.replace('69272', '1')}\n`);
  appendFileSync(join(d, 'chain-x-1.out'), `${line}\nrewind-smoke:   refuse: thread 16004 is inside NtFsControlFile\n`);
  appendFileSync(join(d, 'chain-x-build.out'), 'rewind-smoke:   refuse: toy case\n');
  const ev = backfill(d);
  assert.deepEqual(ev.map(e => [e.queue, e.job ?? '', e.why.length]), [['chain-x', 'bp-seen-dry-crowd', 2], ['chain-x', '', 1]], 'cut reason dropped, copies counted once, build log skipped');
  rmSync(d, { recursive: true, force: true });
  console.log('rewind-refusals self-check ok (kinds and owners on real reasons, new-pair report, tally, backfill)');
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(import.meta.filename)) {
  const [cmd, ...rest] = process.argv.slice(2), opt = (n: string, d: string) => { const i = rest.indexOf(n); return i >= 0 ? rest[i + 1] : d; };
  const file = resolve(opt('--file', REFUSALS));
  if (cmd === '--self-check') selfCheck();
  else if (cmd === 'tally') console.log(tally(file));
  else if (cmd === 'backfill') {
    const ev = backfill(resolve(opt('--from', resolve(import.meta.dirname, '..', '..', 'probe-out', 'queue'))));
    let n = 0;
    for (const e of ev) {
      if (rest.includes('--write')) for (const m of recordRefusals(file, e)) console.log(`new: ${m}`);
      else for (const w of e.why) console.log(`${e.queue}\t${e.job ?? ''}\t${classify(w).kind}\t${classify(w).owner}\t${w.slice(0, 160)}`);
      n += e.why.length;
    }
    console.log(`${n} reasons from ${ev.length} log lines${rest.includes('--write') ? ` appended to ${file}` : ' (dry run: --write appends)'}`);
  } else { console.error('usage: rewind-refusals.ts tally|backfill [--from dir] [--file f] [--write] | --self-check'); process.exit(2); }
}
