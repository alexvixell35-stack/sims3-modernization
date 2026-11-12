// Atomic reservations for the shared numbers in docs/registry.md: ts3native counters versions + offsets and
// ts3fixes counter slots, plus docs/51 inference-ledger section numbers. Parallel worktrees see each other through docs/data/registry-reservations.json in the
// MAIN checkout (found via `git rev-parse --git-common-dir`), guarded by an O_EXCL lock file next to its registry.md.
//   node tools/registry.ts reserve native <n-int32> --by <who> --what <text>
//   node tools/registry.ts reserve fixes <n-slots> --by <who> --what <text>
//   node tools/registry.ts reserve ledger --for <topic> [--by <who>]      next free docs/51 `## N.` section (max of the
//        headings in this worktree's and the main checkout's docs/51 and of every ledger reservation, + 1); docs/51 not touched
//   node tools/registry.ts reserve doc --for <topic> [--by <who>]      next free docs/NNN number (docs/ files + reservations)
//   Add --contributor to any reserve: numbers come from the contributor ranges (CONTRIB below, docs/registry.md "Contributor
//        ranges"); without it, the contributor ranges are skipped.
//   node tools/registry.ts list | release <id> | fix-next <file> | --self-check
// Options: --root <dir> uses <dir> as both worktree and main checkout (tests).
import { execFileSync, spawn } from 'node:child_process';
import { closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readdirSync, readFileSync, rmSync, statSync, unlinkSync, writeFileSync, writeSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';

type Kind = 'fixes' | 'native' | 'ledger' | 'doc';
interface Res { id: string; kind: Kind; from: number; to: number; version?: number; by: string; what: string; at: string; worktree: string }
interface Used { fixesMax: number; nativeVer: number; nativeEnd: number; cFixesMax: number; cNativeVer: number }

// Contributor ranges (docs/registry.md "Contributor ranges"); maintainer numbers never enter them.
// fixes: 2000-2199 (full since the PR #368 import, 2026-11-12), then 2200-2399; FIX_BLOCK2 is where the second block starts.
export const CONTRIB = { doc: [500, 599], ledger: [900, 999], fixes: [2000, 2399], nativeV: 900 } as const;
export const FIX_BLOCK2 = 2200;
// A contributor slot block never straddles 2199/2200: one that would starts at 2200.
export const contribFixFrom = (max: number, n: number) => max < FIX_BLOCK2 && max + n >= FIX_BLOCK2 ? FIX_BLOCK2 : max + 1;
const inR = (n: number, [a, b]: readonly [number, number] | readonly number[]) => n >= a && n <= b;
export const isContribFix = (a: number) => a >= CONTRIB.fixes[0];
export const isContribVer = (v: number) => v >= CONTRIB.nativeV;

export const FIX_HEAD = '## ts3fixes counters';
export const NAT_HEAD = '## ts3native counters (';
const LEDGER = join('docs', '51-inference-ledger.md');
export const ledgerMax = (text: string, contrib = false) => Math.max(contrib ? CONTRIB.ledger[0] - 1 : 0, ...[...text.matchAll(/^## (\d+)\./gm)].map(m => +m[1]).filter(n => inR(n, CONTRIB.ledger) === contrib));
export const docMax = (names: string[], contrib = false) => Math.max(contrib ? CONTRIB.doc[0] - 1 : 0, ...names.map(f => +(f.match(/^(\d+)-.*\.md$/)?.[1] ?? -1)).filter(n => n >= 0 && inR(n, CONTRIB.doc) === contrib));
const hex = (n: number) => '0x' + n.toString(16).toUpperCase().padStart(4, '0');

// Lines of a `## ` section that starts with head: [start, end) indices.
export function section(L: string[], head: string): [number, number] {
  const s = L.findIndex(l => l.startsWith(head));
  if (s < 0) return [-1, -1];
  let e = L.findIndex((l, i) => i > s && l.startsWith('## '));
  return [s, e < 0 ? L.length : e];
}
export const fixRow = (l: string) => { const m = l.match(/^\| (\d+)(?:-(\d+))? \|/); return m ? [+m[1], +(m[2] ?? m[1])] : null; };
export const natRow = (l: string) => {
  const m = l.match(/^\| v(\d+) \|[^|]*\|([^|]*)\|/);
  if (!m) return null;
  const o = m[2].match(/0x([0-9A-Fa-f]+)-0x([0-9A-Fa-f]+)/);
  return { v: +m[1], from: o ? parseInt(o[1], 16) : -1, to: o ? parseInt(o[2], 16) : -1 };
};
const isNext = (l: string) => /^\| \*\*/.test(l);

export function scanDoc(text: string): Used & { collisions: string[] } {
  const L = text.split(/\r?\n/), col: string[] = [];
  let fixesMax = -1, nativeVer = 0, nativeEnd = -1, cFixesMax = CONTRIB.fixes[0] - 1, cNativeVer = CONTRIB.nativeV - 1;
  const [fs, fe] = section(L, FIX_HEAD), slots: number[][] = [];
  for (let i = fs; i >= 0 && i < fe; i++) { const r = fixRow(L[i]); if (r) { slots.push(r); if (isContribFix(r[0])) cFixesMax = Math.max(cFixesMax, r[1]); else fixesMax = Math.max(fixesMax, r[1]); } }
  const [ns, ne] = section(L, NAT_HEAD), vers = new Map<number, number>(), offs: number[][] = [];
  for (let i = ns; i >= 0 && i < ne; i++) {
    const r = natRow(L[i]); if (!r) continue;
    vers.set(r.v, (vers.get(r.v) ?? 0) + 1); if (isContribVer(r.v)) cNativeVer = Math.max(cNativeVer, r.v); else nativeVer = Math.max(nativeVer, r.v);
    if (r.from >= 0) { offs.push([r.from, r.to, r.v]); nativeEnd = Math.max(nativeEnd, r.to); }
  }
  slots.sort((a, b) => a[0] - b[0]);
  for (let i = 1; i < slots.length; i++) if (slots[i][0] <= slots[i - 1][1]) col.push(`ts3fixes slots ${slots[i - 1].join('-')} and ${slots[i].join('-')} overlap`);
  for (const [v, n] of vers) if (n > 1) col.push(`ts3native v${v} taken ${n} times`);
  offs.sort((a, b) => a[0] - b[0]);
  for (let i = 1; i < offs.length; i++) if (offs[i][0] <= offs[i - 1][1]) col.push(`ts3native offsets of v${offs[i - 1][2]} and v${offs[i][2]} overlap at ${hex(offs[i][0])}`);
  return { fixesMax, nativeVer, nativeEnd, cFixesMax, cNativeVer, collisions: col };
}

// Rebuilds both "next free" rows from the highest row in each table.
export function fixNext(text: string): string {
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  let L = text.split(/\r?\n/);
  const u = scanDoc(text);
  const put = (head: string, isRow: (l: string) => unknown, row: string) => {
    const [s, e] = section(L, head); if (s < 0) return;
    const keep = L.slice(s, e).filter(l => !isNext(l));
    let last = -1; keep.forEach((l, i) => { if (isRow(l)) last = i; });
    if (last >= 0) keep.splice(last + 1, 0, row);
    L = [...L.slice(0, s), ...keep, ...L.slice(e)];
  };
  put(FIX_HEAD, fixRow, `| **${u.fixesMax + 1}+** | next free | reserve here  |`);
  put(NAT_HEAD, natRow, `| **v${u.nativeVer + 1}** | next | after v${u.nativeVer} (+${hex(u.nativeEnd + 1)}) | reserve here |`);
  return L.join(eol);
}

const sleep = (ms: number) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
function withLock<T>(lock: string, fn: () => T): T {
  const t0 = Date.now();
  for (;;) {
    try { const fd = openSync(lock, 'wx'); writeSync(fd, `${process.pid} ${new Date().toISOString()}\n`); closeSync(fd); break; }
    catch (e: any) {
      // EPERM/EACCES/EBUSY: on Windows the lock another reserve just deleted is still delete-pending (or held by a scanner)
      if (!['EEXIST', 'EPERM', 'EACCES', 'EBUSY'].includes(e.code)) throw e;
      try { if (Date.now() - statSync(lock).mtimeMs > 120_000) { unlinkSync(lock); continue; } } catch { continue; } // stale lock from a killed run
      if (Date.now() - t0 > 20_000) throw new Error(`registry lock busy for 20 s: ${lock} (delete it if no reserve is running)`);
      sleep(25 + Math.random() * 50);
    }
  }
  try { return fn(); } finally { try { unlinkSync(lock); } catch { /* already gone */ } }
}

function roots(args: string[]) {
  const i = args.indexOf('--root');
  if (i >= 0) { const r = resolve(args[i + 1]); return { wt: r, main: r }; }
  const git = (...a: string[]) => execFileSync('git', a, { encoding: 'utf8' }).trim();
  const wt = resolve(git('rev-parse', '--show-toplevel'));
  const common = resolve(wt, git('rev-parse', '--git-common-dir'));
  return { wt, main: common.endsWith('.git') ? dirname(common) : wt };
}
const jsonPath = (main: string) => join(main, 'docs', 'data', 'registry-reservations.json');
const loadRes = (main: string): Res[] => existsSync(jsonPath(main)) ? JSON.parse(readFileSync(jsonPath(main), 'utf8')).reservations : [];
function saveRes(main: string, r: Res[]) {
  mkdirSync(dirname(jsonPath(main)), { recursive: true });
  writeFileSync(jsonPath(main), JSON.stringify({ note: 'written by tools/registry.ts; see docs/registry.md', reservations: r }, null, 2) + '\n');
}
const opt = (args: string[], k: string) => { const i = args.indexOf(k); return i >= 0 ? args[i + 1] : undefined; };

function reserveLedger(args: string[], c: boolean) {
  const what = opt(args, '--for'), { wt, main } = roots(args), by = opt(args, '--by') ?? basename(wt);
  if (!what) throw new Error('usage: reserve ledger --for <topic> [--by <who>]');
  return withLock(join(main, 'docs', 'registry.md.lock'), () => {
    const res = loadRes(main), docs = [...new Set([join(wt, LEDGER), join(main, LEDGER)])].filter(existsSync);
    const n = Math.max(c ? CONTRIB.ledger[0] - 1 : 0, ...docs.map(d => ledgerMax(readFileSync(d, 'utf8'), c)), ...res.filter(r => r.kind === 'ledger' && inR(r.to, CONTRIB.ledger) === c).map(r => r.to)) + 1;
    if (c && n > CONTRIB.ledger[1]) throw new Error(`contributor ledger range ${CONTRIB.ledger.join('-')} is full`);
    const r: Res = { id: `ledger-${n}`, kind: 'ledger', from: n, to: n, by, what, at: new Date().toISOString(), worktree: wt };
    saveRes(main, [...res, r]);
    console.log(`reserved docs/51 §${n} id=${r.id}: write it as "## ${n}. ..." (unused: node tools/registry.ts release ${r.id})`);
    return r;
  });
}

function reserveDoc(args: string[], c: boolean) {
  const what = opt(args, '--for'), { wt, main } = roots(args), by = opt(args, '--by') ?? basename(wt);
  if (!what) throw new Error('usage: reserve doc --for <topic> [--by <who>]');
  return withLock(join(main, 'docs', 'registry.md.lock'), () => {
    const res = loadRes(main), dirs = [...new Set([join(wt, 'docs'), join(main, 'docs')])].filter(existsSync);
    const n = Math.max(c ? CONTRIB.doc[0] - 1 : 0, ...dirs.map(d => docMax(readdirSync(d), c)), ...res.filter(r => r.kind === 'doc' && inR(r.to, CONTRIB.doc) === c).map(r => r.to)) + 1;
    if (c && n > CONTRIB.doc[1]) throw new Error(`contributor doc range ${CONTRIB.doc.join('-')} is full`);
    const r: Res = { id: `doc-${n}`, kind: 'doc', from: n, to: n, by, what, at: new Date().toISOString(), worktree: wt };
    saveRes(main, [...res, r]);
    console.log(`reserved docs/${n}-*.md id=${r.id} (unused: node tools/registry.ts release ${r.id})`);
    return r;
  });
}

function reserve(args: string[], c: boolean) {
  if (args[1] === 'ledger') return reserveLedger(args, c);
  if (args[1] === 'doc') return reserveDoc(args, c);
  const kind = args[1] as Kind, n = Number(args[2]), by = opt(args, '--by'), what = opt(args, '--what');
  if (!['fixes', 'native'].includes(kind) || !(n >= 1 && Number.isInteger(n)) || !by || !what) throw new Error('usage: reserve fixes|native <n> --by <who> --what <text>');
  const { wt, main } = roots(args);
  const docs = [...new Set([join(wt, 'docs', 'registry.md'), join(main, 'docs', 'registry.md')])].filter(existsSync);
  if (!docs.length) throw new Error('docs/registry.md not found');
  return withLock(join(main, 'docs', 'registry.md.lock'), () => {
    const res = loadRes(main);
    const u = docs.map(d => scanDoc(readFileSync(d, 'utf8')));
    let fixesMax = Math.max(...u.map(x => c ? x.cFixesMax : x.fixesMax)), nativeVer = Math.max(...u.map(x => c ? x.cNativeVer : x.nativeVer)), nativeEnd = Math.max(...u.map(x => x.nativeEnd));
    for (const r of res) if (r.kind === 'fixes') { if (isContribFix(r.from) === c) fixesMax = Math.max(fixesMax, r.to); } else if (r.kind === 'native') { if (isContribVer(r.version!) === c) nativeVer = Math.max(nativeVer, r.version!); nativeEnd = Math.max(nativeEnd, r.to); }
    if (c && kind === 'fixes') fixesMax = contribFixFrom(fixesMax, n) - 1;
    if (c && kind === 'fixes' && fixesMax + n > CONTRIB.fixes[1]) throw new Error(`contributor slot range ${CONTRIB.fixes.join('-')} has ${CONTRIB.fixes[1] - fixesMax} left, asked ${n}`);
    const at = new Date().toISOString();
    let r: Res, row: string;
    if (kind === 'fixes') {
      r = { id: `fixes-${fixesMax + 1}`, kind, from: fixesMax + 1, to: fixesMax + n, by, what, at, worktree: wt };
      row = `| ${r.from === r.to ? r.from : `${r.from}-${r.to}`} | reserved (${by}, \`${r.id}\`) | ${what} |`;
    } else {
      const from = (nativeEnd + 4) & ~3;
      r = { id: `native-v${nativeVer + 1}`, kind, version: nativeVer + 1, from, to: from + 4 * n - 1, by, what, at, worktree: wt };
      row = `| v${r.version} | reserved (${by}, \`${r.id}\`) | ${hex(r.from)}-${hex(r.to)} | ${what} |`;
    }
    saveRes(main, [...res, r]);
    const doc = join(wt, 'docs', 'registry.md');
    if (existsSync(doc)) {
      const L = readFileSync(doc, 'utf8').split(/\r?\n/);
      const [s, e] = section(L, kind === 'fixes' ? FIX_HEAD : NAT_HEAD);
      let at2 = -1; for (let i = s; i < e; i++) if (isNext(L[i]) || (kind === 'fixes' ? fixRow(L[i]) : natRow(L[i]))) at2 = i;
      if (s >= 0) { L.splice(at2 + 1, 0, row); writeFileSync(doc, fixNext(L.join('\n'))); }
    }
    const range = kind === 'fixes' ? `slots ${r.from}-${r.to} (int[${r.to + 1}])` : `v${r.version} ${hex(r.from)}-${hex(r.to)} (${n} int32)`;
    console.log(`reserved ${kind} ${range} id=${r.id}`);
    return r;
  });
}

function release(args: string[]) {
  const id = args[1], { wt, main } = roots(args);
  withLock(join(main, 'docs', 'registry.md.lock'), () => {
    const res = loadRes(main), r = res.find(x => x.id === id);
    if (!r) throw new Error(`no reservation ${id}`);
    saveRes(main, res.filter(x => x !== r));
    const doc = join(wt, 'docs', 'registry.md');
    if (existsSync(doc)) writeFileSync(doc, fixNext(readFileSync(doc, 'utf8').split(/\r?\n/).filter(l => !l.includes(`\`${id}\``)).join('\n')));
    console.log(`released ${id}`);
  });
}

function list(args: string[]) {
  const { wt, main } = roots(args), u = scanDoc(readFileSync(join(wt, 'docs', 'registry.md'), 'utf8'));
  console.log(`doc: ts3fixes highest slot ${u.fixesMax}, ts3native v${u.nativeVer} ends ${hex(u.nativeEnd)}`);
  for (const r of loadRes(main)) console.log(`${r.id}\t${r.kind === 'fixes' ? `${r.from}-${r.to}` : r.kind === 'ledger' ? `docs/51 §${r.from}` : r.kind === 'doc' ? `docs/${r.from}` : `v${r.version} ${hex(r.from)}-${hex(r.to)}`}\t${r.by}\t${r.at}\t${r.what}`);
  for (const c of u.collisions) console.log(`COLLISION: ${c}`);
}

async function selfCheck() {
  const d = mkdtempSync(join(tmpdir(), 'registry-check-'));
  const ok = (c: unknown, m: string) => { if (!c) { console.error('FAIL', m); process.exitCode = 1; } else console.log('ok  ', m); };
  try {
    mkdirSync(join(d, 'docs'));
    writeFileSync(join(d, 'docs', 'registry.md'), [
      '# Registry', '', `${FIX_HEAD} (\`int[255]\`)`, '', '| Slots | Owner | Meaning |', '|---|---|---|',
      '| 0-3 | a | b |', '| 19-20 | free | never used |', '| 248-254 | x | y |', '| **255+** | next free | reserve here  |', '',
      `${NAT_HEAD}\`Ts3nCounters\`)`, '', '| Version | ts3native | Offsets | Fields |', '|---|---|---|---|',
      '| v1 | 0.2.0 | 0x00-0x1C | a |', '| v110 | 0.36.39 | 0x4628-0x4647 | b |', '| **v111** | next | after v110 (+0x4648) | reserve here |', '',
      '## ts3native `ts3native_mtprobe` export', '', '| a | b |', ''].join('\n'));
    // today's collisions: another worktree already took v111 at +0x4648 and slots 255-259, not merged into this doc
    saveRes(d, [
      { id: 'native-v111', kind: 'native', version: 111, from: 0x4648, to: 0x465F, by: 'other', what: 'other branch', at: '', worktree: 'w2' },
      { id: 'fixes-255', kind: 'fixes', from: 255, to: 259, by: 'other', what: 'other branch', at: '', worktree: 'w2' },
      { id: 'ledger-316', kind: 'ledger', from: 316, to: 316, by: 'other', what: 'other branch', at: '', worktree: 'w2' }]);
    const ledgerText = '# 51. Ledger\n\n## 314. a\n\n## 315. b, see ledger §9\n\ntext ## 400. not a heading\n';
    writeFileSync(join(d, LEDGER), ledgerText);
    const self = import.meta.filename;
    const run = (a: string[]) => new Promise<number>(res => spawn(process.execPath, [self, ...a, '--root', d], { stdio: ['ignore', 'ignore', 'inherit'] }).on('exit', c => res(c ?? 1)));
    const codes = await Promise.all([...Array(6)].flatMap((_, i) => [
      run(['reserve', 'native', '6', '--by', `n${i}`, '--what', `native ${i}`]), run(['reserve', 'fixes', '5', '--by', `f${i}`, '--what', `fixes ${i}`]),
      run(['reserve', 'ledger', '--for', `topic ${i}`])]));
    ok(codes.every(c => c === 0), '18 concurrent reserves all exit 0');
    const lg = loadRes(d).filter(r => r.kind === 'ledger').map(r => r.from).sort((a, b) => a - b);
    ok(lg.join() === '316,317,318,319,320,321,322', `ledger sections 317-322 handed out once each after the reserved 316 (${lg.join()})`);
    ok(readFileSync(join(d, LEDGER), 'utf8') === ledgerText, 'docs/51 itself untouched');
    const res = loadRes(d), fx = res.filter(r => r.kind === 'fixes').sort((a, b) => a.from - b.from), nt = res.filter(r => r.kind === 'native').sort((a, b) => a.from - b.from);
    ok(fx.length === 7 && nt.length === 7, '7 + 7 reservations in the machine file');
    ok(fx.every((r, i) => !i || r.from > fx[i - 1].to) && fx[1].from === 260, `fixes ranges disjoint, first new one 260 (${fx.map(r => r.from + '-' + r.to).join(' ')})`);
    ok(nt.every((r, i) => !i || (r.from > nt[i - 1].to && r.version! > nt[i - 1].version!)) && nt[1].version === 112 && nt[1].from === 0x4660, `native versions unique, offsets disjoint, first new one v112 at 0x4660`);
    const doc = readFileSync(join(d, 'docs', 'registry.md'), 'utf8'), sc = scanDoc(doc);
    ok(sc.collisions.length === 0, 'doc has no collisions');
    ok(doc.includes('| **290+** | next free |') && doc.includes('| **v118** | next | after v117 (+0x46F0) |'), 'next rows recomputed (290+, v118 at 0x46F0)');
    ok(doc.split('\n').filter(isNext).length === 2 && doc.includes('## ts3native `ts3native_mtprobe` export'), 'one next row per table, later sections kept');
    await new Promise<void>(r => spawn(process.execPath, [self, 'release', 'fixes-285', '--root', d], { stdio: 'ignore' }).on('exit', () => r()));
    ok(!loadRes(d).some(r => r.id === 'fixes-285') && readFileSync(join(d, 'docs', 'registry.md'), 'utf8').includes('| **285+** | next free |'), 'release of the top fixes range frees it');
    const dup = fixNext(doc.replace('| v113 |', '| v112 |'));
    ok(scanDoc(dup).collisions.some(c => c.includes('v112 taken 2')), 'fix-next reports a version taken twice');
    // contributor ranges: own numbers, maintainer numbers unaffected (and the other way round)
    writeFileSync(join(d, 'docs', '12-a.md'), 'x'); writeFileSync(join(d, 'docs', '501-b.md'), 'x');
    const r1 = (a: string[]) => run(a);
    await r1(['reserve', '--contributor', 'fixes', '5', '--by', 'c', '--what', 'c1']); await r1(['reserve', 'fixes', '2', '--contributor', '--by', 'c', '--what', 'c2']);
    await r1(['reserve', 'native', '2', '--contributor', '--by', 'c', '--what', 'cn']); await r1(['reserve', 'native', '--range', 'contributor', '2', '--by', 'c', '--what', 'cn2']);
    await r1(['reserve', 'ledger', '--contributor', '--for', 'c']); await r1(['reserve', 'doc', '--contributor', '--for', 'c']); await r1(['reserve', 'doc', '--for', 'm']);
    const cr = loadRes(d), cf = cr.filter(r => r.kind === 'fixes' && r.from >= 2000), cn = cr.filter(r => r.kind === 'native' && r.version! >= 900);
    ok(cf.map(r => r.from + '-' + r.to).join() === '2000-2004,2005-2006', `contributor fixes 2000-2004, 2005-2006 (${cf.map(r => r.from + '-' + r.to)})`);
    ok(cn.map(r => r.version).join() === '900,901' && cn[0].from >= 0x46F0, 'contributor native v900, v901, offsets past the maintainer ones');
    ok(cr.some(r => r.id === 'ledger-900') && cr.some(r => r.id === 'doc-502') && cr.some(r => r.id === 'doc-52'), 'contributor ledger 900, doc 502 (501 exists); maintainer doc 52 (after docs/51)');
    await r1(['reserve', 'fixes', '1', '--by', 'm', '--what', 'm']); await r1(['reserve', 'native', '1', '--by', 'm', '--what', 'm']); await r1(['reserve', 'ledger', '--for', 'm']);
    const mr = loadRes(d);
    ok(mr.some(r => r.kind === 'fixes' && r.from === 285) && mr.some(r => r.kind === 'native' && r.version === 118) && mr.some(r => r.id === 'ledger-323'), 'maintainer reserves skip the contributor ranges (fixes 285, native v118, ledger 323)');
    ok(scanDoc(readFileSync(join(d, 'docs', 'registry.md'), 'utf8')).collisions.length === 0, 'doc has no collisions with contributor rows');
    ok(!existsSync(join(d, 'docs', 'registry.md.lock')), 'lock file removed');
    // second contributor block: a block that would cross 2199/2200 starts at 2200; 2399 is the end
    ok(contribFixFrom(2006, 5) === 2007 && contribFixFrom(2195, 5) === 2200 && contribFixFrom(2195, 4) === 2196 && contribFixFrom(2303, 3) === 2304, 'contributor fixes: blocks stay inside 2000-2199 or start at 2200');
    ok(CONTRIB.fixes[1] === 2399, 'contributor fixes range ends at 2399');
  } finally { rmSync(d, { recursive: true, force: true }); }
}

const contributor = process.argv.includes('--contributor') || opt(process.argv, '--range') === 'contributor';
const args = process.argv.slice(2).filter((a, i, v) => a !== '--contributor' && a !== '--range' && v[i - 1] !== '--range');
if (process.argv[1]?.endsWith('registry.ts')) try {  // importable (tools/pr-preflight.ts)
  if (args[0] === '--self-check') await selfCheck();
  else if (args[0] === 'reserve') reserve(args, contributor);
  else if (args[0] === 'release') release(args);
  else if (args[0] === 'list') list(args);
  else if (args[0] === 'fix-next') {
    const t = readFileSync(args[1], 'utf8'), out = fixNext(t);
    if (out !== t) writeFileSync(args[1], out);
    for (const c of scanDoc(out).collisions) console.log(`COLLISION: ${c}`);
  } else { console.error('usage: node tools/registry.ts [--contributor] reserve fixes|native <n> --by <who> --what <text> | reserve doc|ledger --for <topic> [--by <who>] | list | release <id> | fix-next <file> | --self-check'); process.exit(2); }
} catch (e: any) { console.error(e.message); process.exit(1); }
