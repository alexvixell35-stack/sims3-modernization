// What a branch or PR clashes with on the base, before anyone reviews it (CONTRIBUTING.md "Before opening a PR"):
// new docs/NNN numbers the base already has, docs/51 ledger sections, counter slots and versions (docs/registry.md and
// docs/data/registry-reservations.json), ts3native version strings, files both sides changed (git merge-tree), index
// lines in CLAUDE.md / PLAN.md that name a clashing doc, then check-docs-code and the changed scripts' self-checks.
//   node tools/pr-preflight.ts [--base origin/main] [--pr <n>] [--repo <owner/name>] [--all] [--json] [--no-checks] [--no-prs] [--maintainer]
// Contributor ranges are checked by default (new doc / docs/51 § / ts3fixes slot / ts3native counters version outside them,
// and a changed TS3NATIVE_VERSION, are problems); --maintainer turns that off.
//   node tools/pr-preflight.ts --self-check
// Default output: one summary line, the problems, and "needs review: ..." last. --all adds what is fine, --json all of it.
// --pr <n> fetches the PR head (gh) from the base's remote; checks then run in a temporary worktree. --no-prs skips the
// open-PR doc numbers (gh). Read-only on the repo: nothing is committed, and the temporary worktree is removed.
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { CONTRIB, FIX_HEAD, NAT_HEAD, fixRow, natRow, section } from './registry.ts';

type Kind = 'range' | 'doc' | 'ledger' | 'registry' | 'version' | 'conflict' | 'index' | 'check' | 'note';
export interface Finding { kind: Kind; problem: boolean; text: string }
// One side of the comparison: file text at a ref (null = missing) and the docs/ listing.
export interface Tree { read(path: string): string | null; docs: string[] }
export interface Input {
  mb: Tree; base: Tree; head: Tree;
  headAdded: string[];               // paths the branch adds (renames count as added)
  headDiff: Map<string, string[]>;   // path -> lines the branch adds (merge-base..head)
  conflicts: string[];               // git merge-tree: files that do not merge
  bothChanged: string[];             // files both sides changed since the merge-base
  openClaims: Map<number, number[]>; // new doc numbers other open PRs add -> PR numbers
  baseName: string;
  ranges?: boolean;                  // contributor mode: new numbers must sit in the CONTRIB ranges (docs/registry.md "Contributor ranges")
}

const LEDGER = 'docs/51-inference-ledger.md', REGISTRY = 'docs/registry.md', RES = 'docs/data/registry-reservations.json';
const NATIVE_CPP = 'native/ts3native/ts3native.cpp';
const docNum = (p: string) => { const m = p.match(/^docs\/(\d+)-[^/]*\.md$/); return m ? +m[1] : null; };
const ledgerHeads = (t: string | null) => new Map([...(t ?? '').matchAll(/^## (\d+)\.\s*(.*)$/gm)].map(m => [+m[1], m[2].trim()] as [number, string]));
const nativeVersion = (t: string | null) => t?.match(/#define TS3NATIVE_VERSION "(\d+)\.(\d+)\.(\d+)"/)?.slice(1).map(Number) ?? null;
const cmpVer = (a: number[], b: number[]) => a[0] - b[0] || a[1] - b[1] || a[2] - b[2];
const vstr = (v: number[]) => v.join('.');
const reservations = (t: string | null): { id: string; kind: string; from: number; to: number; version?: number; what: string }[] => {
  try { return t ? JSON.parse(t).reservations ?? [] : []; } catch { return []; }
};

// Registry rows of one table: [from, to] slot ranges (fixes) or versions (native), keyed by the row's line text.
function registryRows(t: string | null) {
  const L = (t ?? '').split(/\r?\n/), fixes = new Map<string, [number, number]>(), native = new Map<string, number>();
  const [fs, fe] = section(L, FIX_HEAD), [ns, ne] = section(L, NAT_HEAD);
  for (let i = fs; i >= 0 && i < fe; i++) { const r = fixRow(L[i]); if (r && !/^\| \*\*/.test(L[i])) fixes.set(L[i].trim(), r as [number, number]); }
  for (let i = ns; i >= 0 && i < ne; i++) { const r = natRow(L[i]); if (r) native.set(L[i].trim(), r.v); }
  return { fixes, native };
}

export function analyze(x: Input): Finding[] {
  const out: Finding[] = [], bad = (kind: Kind, text: string) => out.push({ kind, problem: true, text }), ok = (kind: Kind, text: string) => out.push({ kind, problem: false, text });
  // docs/NNN numbers
  const baseNums = new Map<number, string>(), clash = new Map<number, number>();
  for (const d of x.base.docs) { const n = docNum(d); if (n !== null) baseNums.set(n, d); }
  const newDocs = x.headAdded.filter(p => docNum(p) !== null && !x.base.docs.includes(p));
  const taken = new Set([...baseNums.keys(), ...newDocs.map(p => docNum(p)!)]);
  let next = Math.max(0, ...baseNums.keys()) + 1;
  const free = () => { while (taken.has(next) || x.openClaims.has(next)) next++; taken.add(next); return next; };
  const seen = new Map<number, string>();
  for (const p of newDocs.sort()) {
    const n = docNum(p)!, on = baseNums.get(n), twice = seen.get(n);
    seen.set(n, p);
    if (on || twice) { const to = free(); clash.set(n, to); bad('doc', `${p}: ${n} is taken ${on ? `on ${x.baseName} by ${on}` : `by this branch's ${twice}`}; next free: ${to}`); }
    else if (x.openClaims.has(n)) bad('doc', `${p}: open PR ${x.openClaims.get(n)!.map(k => '#' + k).join(', ')} also adds docs/${n}; whichever merges second renumbers`);
    else ok('doc', `${p}: ${n} is free on ${x.baseName}`);
  }
  // CLAUDE.md / PLAN.md lines this branch adds that name a clashing doc number
  for (const f of ['CLAUDE.md', 'PLAN.md', 'docs/index.md']) for (const l of x.headDiff.get(f) ?? []) for (const [n, to] of clash)
    if (new RegExp(`docs/${n}\\b`).test(l)) bad('index', `${f}: an added line names docs/${n}; make it docs/${to}: ${l.trim().slice(0, 90)}`);
  // docs/51 ledger sections the branch adds
  const mbL = ledgerHeads(x.mb.read(LEDGER)), baseL = ledgerHeads(x.base.read(LEDGER)), headL = ledgerHeads(x.head.read(LEDGER));
  const baseRes = reservations(x.base.read(RES)), headRes = reservations(x.head.read(RES)), headIds = new Set(headRes.map(r => r.id));
  const otherRes = baseRes.filter(r => !headIds.has(r.id));
  let nextL = Math.max(0, ...baseL.keys(), ...headL.keys(), ...baseRes.filter(r => r.kind === 'ledger').map(r => r.to)) + 1;
  for (const [n, title] of headL) {
    if (mbL.has(n)) continue;
    const on = baseL.get(n), res = otherRes.find(r => r.kind === 'ledger' && r.from <= n && n <= r.to);
    if (on !== undefined && on !== title) bad('ledger', `docs/51 §${n} "${title.slice(0, 50)}": ${x.baseName} has §${n} "${on.slice(0, 50)}"; next free: §${nextL++}`);
    else if (res) bad('ledger', `docs/51 §${n}: reserved on ${x.baseName} by ${res.id} (${res.what.slice(0, 50)}); next free: §${nextL++}`);
    else ok('ledger', `docs/51 §${n} is free`);
  }
  // counter slots and versions: rows the branch adds against rows and reservations the base has and the branch does not
  const mbR = registryRows(x.mb.read(REGISTRY)), baseR = registryRows(x.base.read(REGISTRY)), headR = registryRows(x.head.read(REGISTRY));
  const baseFix = [...baseR.fixes].filter(([k]) => !headR.fixes.has(k)), baseNat = [...baseR.native].filter(([k]) => !headR.native.has(k));
  for (const [line, [a, b]] of headR.fixes) {
    if (mbR.fixes.has(line)) continue;
    const hit = baseFix.find(([, [c, d]]) => a <= d && c <= b)?.[0] ?? otherRes.find(r => r.kind === 'fixes' && a <= r.to && r.from <= b)?.id;
    if (hit) bad('registry', `ts3fixes slots ${a}-${b}: ${x.baseName} already uses them (${hit.slice(0, 70)}); reserve again with tools/registry.ts`);
    else ok('registry', `ts3fixes slots ${a}-${b} are free`);
  }
  for (const [line, v] of headR.native) {
    if (mbR.native.has(line)) continue;
    const hit = baseNat.find(([, w]) => w === v)?.[0] ?? otherRes.find(r => r.kind === 'native' && r.version === v)?.id;
    if (hit) bad('registry', `ts3native counters v${v}: ${x.baseName} already has it (${hit.slice(0, 70)}); reserve again with tools/registry.ts`);
    else ok('registry', `ts3native counters v${v} is free`);
  }
  // ts3native version string: a bump that the base already passed, and versions the branch's added lines name
  const vMb = nativeVersion(x.mb.read(NATIVE_CPP)), vBase = nativeVersion(x.base.read(NATIVE_CPP)), vHead = nativeVersion(x.head.read(NATIVE_CPP));
  if (vMb && vBase && vHead) {
    const bumped = cmpVer(vHead, vMb) !== 0;
    if (bumped && cmpVer(vHead, vBase) <= 0 && !x.ranges) bad('version', `TS3NATIVE_VERSION ${vstr(vHead)}: ${x.baseName} is already at ${vstr(vBase)}; use ${vBase[0]}.${vBase[1]}.${vBase[2] + 1}`);
    else if (bumped) ok('version', `TS3NATIVE_VERSION ${vstr(vHead)} is past ${x.baseName}'s ${vstr(vBase)}`);
    const named = new Map<string, string>();
    for (const [f, lines] of x.headDiff) for (const l of lines) for (const m of l.matchAll(/\b(\d+)\.(\d+)\.(\d+)\b/g)) {
      const v = [+m[1], +m[2], +m[3]];
      if (v[0] === vBase[0] && v[1] === vBase[1] && cmpVer(v, vMb) > 0 && cmpVer(v, vBase) <= 0 && !named.has(vstr(v))) named.set(vstr(v), f);
    }
    for (const [v, f] of named) bad('version', `${f} names ts3native ${v}, which ${x.baseName} has used since this branch started (now ${vstr(vBase)})`);
  }
  if (x.ranges) {
    const [d0, d1] = CONTRIB.doc, [l0, l1] = CONTRIB.ledger, [f0, f1] = CONTRIB.fixes, v0 = CONTRIB.nativeV;
    const note = (k: string) => `outside the contributor range (docs/registry.md "Contributor ranges"); ${k}`;
    // docs
    let nd = Math.max(d0 - 1, ...[...baseNums.keys(), ...newDocs.map(p => docNum(p)!), ...x.openClaims.keys()].filter(n => n >= d0 && n <= d1)) + 1;
    for (const p of newDocs.sort()) { const n = docNum(p)!; if (n < d0 || n > d1) bad('range', `${p}: ${n} is ${note(`next free: ${nd++} (node tools/registry.ts reserve --contributor doc --for <topic>)`)}`); }
    // docs/51
    let nl = Math.max(l0, ...[...baseL.keys(), ...headL.keys(), ...baseRes.filter(r => r.kind === 'ledger').map(r => r.to)].filter(n => n >= l0 && n <= l1).map(n => n + 1));
    for (const [n, title] of headL) if (!mbL.has(n) && (n < l0 || n > l1)) bad('range', `docs/51 §${n} "${title.slice(0, 40)}": ${note(`next free: §${nl++} (reserve --contributor ledger --for <topic>)`)}`);
    // ts3fixes slots and ts3native counters versions
    let nf = Math.max(f0, ...[...baseR.fixes.values(), ...headR.fixes.values(), ...baseRes.filter(r => r.kind === 'fixes').map(r => [r.from, r.to] as [number, number])].filter(([a]) => a >= f0).map(([, b]) => b + 1));
    for (const [line, [a, b]] of headR.fixes) if (!mbR.fixes.has(line) && (a < f0 || b > f1)) { bad('range', `ts3fixes slots ${a}-${b}: ${note(`next free: ${nf}-${nf + b - a} (reserve --contributor fixes <n> --by <who> --what <text>)`)}`); nf += b - a + 1; }
    let nv = Math.max(v0, ...[...baseR.native.values(), ...headR.native.values(), ...baseRes.filter(r => r.kind === 'native').map(r => r.version!)].filter(v => v >= v0).map(v => v + 1));
    for (const [line, v] of headR.native) if (!mbR.native.has(line) && v < v0) bad('range', `ts3native counters v${v}: ${note(`next free: v${nv++} (reserve --contributor native <n> --by <who> --what <text>)`)}`);
    // ts3native version string: maintainers assign it at merge
    const a = nativeVersion(x.mb.read(NATIVE_CPP)), b = nativeVersion(x.head.read(NATIVE_CPP));
    if (a && b && cmpVer(a, b) !== 0) bad('range', `TS3NATIVE_VERSION changed (${vstr(a)} -> ${vstr(b)}): maintainers assign ts3native versions at merge; keep the define as on ${x.baseName} and write "next version" in docs and changelog`);
  }
  // files
  if (x.conflicts.length) bad('conflict', `rebase conflicts with ${x.baseName}: ${x.conflicts.join(', ')}`);
  const clean = x.bothChanged.filter(f => !x.conflicts.includes(f));
  if (clean.length) ok('conflict', `both sides changed, merges cleanly: ${clean.join(', ')}`);
  return out;
}

// --- git plumbing ---
const run = (cmd: string, args: string[], cwd: string) => execFileSync(cmd, args, { cwd, encoding: 'utf8', maxBuffer: 256 << 20, stdio: ['ignore', 'pipe', 'pipe'] });
function tree(cwd: string, ref: string): Tree {
  const cache = new Map<string, string | null>();
  return {
    read: p => { if (!cache.has(p)) { try { cache.set(p, run('git', ['show', `${ref}:${p}`], cwd)); } catch { cache.set(p, null); } } return cache.get(p)!; },
    docs: run('git', ['ls-tree', '--name-only', ref, 'docs/'], cwd).split('\n').filter(Boolean),
  };
}
function addedLines(diff: string) {
  const m = new Map<string, string[]>();
  let file = '';
  for (const l of diff.split('\n')) {
    if (l.startsWith('+++ ')) { file = l.slice(4).replace(/^b\//, ''); m.set(file, []); }
    else if (l.startsWith('+') && file) m.get(file)!.push(l.slice(1));
  }
  return m;
}
export function gather(cwd: string, baseRef: string, headRef: string, claims: Map<number, number[]>): Input {
  const mbRef = run('git', ['merge-base', baseRef, headRef], cwd).trim();
  const status = run('git', ['diff', '--name-status', '-M', mbRef, headRef], cwd).split('\n').filter(Boolean).map(l => l.split('\t'));
  const headAdded = status.filter(s => s[0] === 'A' || s[0].startsWith('R')).map(s => s[s.length - 1]);
  const headFiles = new Set(status.map(s => s[s.length - 1]));
  const baseFiles = run('git', ['diff', '--name-only', mbRef, baseRef], cwd).split('\n').filter(Boolean);
  const mt = spawnSync('git', ['merge-tree', '--write-tree', '--name-only', '--no-messages', baseRef, headRef], { cwd, encoding: 'utf8' });
  const conflicts = mt.status === 1 ? mt.stdout.split('\n').slice(1).filter(Boolean) : [];
  if (mt.status !== 0 && mt.status !== 1) throw new Error(`git merge-tree failed (needs git 2.38+): ${mt.stderr.trim()}`);
  return {
    mb: tree(cwd, mbRef), base: tree(cwd, baseRef), head: tree(cwd, headRef), headAdded,
    headDiff: addedLines(run('git', ['diff', '-U0', mbRef, headRef], cwd)),
    conflicts, bothChanged: baseFiles.filter(f => headFiles.has(f)), openClaims: claims, baseName: baseRef,
  };
}

// New doc numbers other open PRs add (gh): number -> PR numbers. null when gh is not there.
function openClaims(cwd: string, repo: string | undefined, self: number | undefined, baseDocs: string[]) {
  try {
    const prs = JSON.parse(run('gh', ['pr', 'list', '--state', 'open', '--limit', '200', '--json', 'number,files', ...(repo ? ['-R', repo] : [])], cwd));
    const m = new Map<number, number[]>();
    for (const p of prs) if (p.number !== self) for (const f of p.files ?? []) {
      const n = docNum(f.path);
      if (n !== null && !baseDocs.includes(f.path)) m.set(n, [...new Set([...(m.get(n) ?? []), p.number])]);
    }
    return m;
  } catch { return null; }
}

// check-docs-code and the self-checks of changed scripts (selftest-all.ts --only), in dir
function checks(dir: string, changed: string[]): Finding[] {
  const out: Finding[] = [];
  const node = (args: string[]) => spawnSync(process.execPath, args, { cwd: dir, encoding: 'utf8', timeout: 600_000 });
  const d = node(['tools/check-docs-code.ts']);
  out.push({ kind: 'check', problem: d.status !== 0, text: `check-docs-code: ${d.status === 0 ? 'ok' : 'FAILED: ' + (d.stdout + d.stderr).trim().split('\n').slice(-3).join(' | ')}` });
  const scripts = changed.filter(f => /^(tools|mods)\/.*\.(ts|mts|cts|js|mjs|cjs)$/.test(f));
  if (scripts.length) {
    const s = node(['tools/selftest-all.ts', ...scripts.flatMap(f => ['--only', f])]);
    const fails = (s.stdout + s.stderr).split('\n').filter(l => /\b(FAIL|TIMEOUT|compile error|no self-check)\b/i.test(l));
    out.push({ kind: 'check', problem: s.status !== 0, text: `self-checks of ${scripts.length} changed script(s): ${s.status === 0 ? 'ok' : 'FAILED: ' + (fails.slice(0, 3).join(' | ') || (s.stdout + s.stderr).trim().split('\n').slice(-2).join(' | '))}` });
  }
  return out;
}

function report(head: string, x: Input | null, f: Finding[], all: boolean) {
  const probs = f.filter(i => i.problem);
  console.log(`pr-preflight: ${head} vs ${x?.baseName ?? '?'}: ${probs.length ? `${probs.length} problem${probs.length > 1 ? 's' : ''}` : 'no problems'}`);
  for (const i of f) if (i.problem || all) console.log(`  ${i.problem ? '' : 'ok: '}${i.kind}: ${i.text}`);
  const kinds = [...new Set(probs.map(i => i.kind))];
  console.log(`needs review: ${probs.length ? `${probs.length} item${probs.length > 1 ? 's' : ''} (${kinds.join(', ')})` : 'none'}`);
}

async function selfCheck() {
  let n = 0;
  const t = (c: unknown, m: string) => { console.assert(c, m); if (!c) process.exitCode = 1; n++; };
  // a throwaway repo: base and branch both start at mb
  const dir = mkdtempSync(join(tmpdir(), 'pr-preflight-'));
  const g = (...a: string[]) => run('git', a, dir);
  const put = (files: Record<string, string>) => { for (const [p, s] of Object.entries(files)) { mkdirSync(dirname(join(dir, p)), { recursive: true }); writeFileSync(join(dir, p), s); } };
  const reg = (fix: string[], nat: string[]) => `# Registry\n\n${FIX_HEAD} (x)\n\n| Slots | Owner | Meaning |\n|---|---|---|\n${fix.join('\n')}\n\n${NAT_HEAD}x)\n\n| Version | Owner | Fields | Meaning |\n|---|---|---|---|\n${nat.join('\n')}\n`;
  try {
    g('init', '-q', '-b', 'main'); g('config', 'user.email', 't@example.invalid'); g('config', 'user.name', 't'); g('config', 'core.autocrlf', 'false');
    put({
      'docs/10-a.md': '# a\n', 'docs/11-b.md': '# b\n', [LEDGER]: '# L\n\n## 1. one\n\n## 2. two\n',
      [REGISTRY]: reg(['| 0-3 | a | x |'], ['| v1 | a | 0x0000-0x0010 | x |']), [RES]: '{"reservations":[]}\n',
      [NATIVE_CPP]: '#define TS3NATIVE_VERSION "0.36.10"\n', 'CLAUDE.md': '- docs/10\n', 'shared.txt': 'one\ntwo\nthree\n',
    });
    g('add', '-A'); g('commit', '-qm', 'mb');
    g('checkout', '-qb', 'branch');
    put({
      'docs/12-mine.md': '# mine\n', 'docs/13-other.md': '# other\n', [LEDGER]: '# L\n\n## 1. one\n\n## 2. two\n\n## 3. mine\n',
      [REGISTRY]: reg(['| 0-3 | a | x |', '| 4-5 | me | y |'], ['| v1 | a | 0x0000-0x0010 | x |', '| v2 | me | 0x0014-0x0020 | y |']),
      [NATIVE_CPP]: '#define TS3NATIVE_VERSION "0.36.11"\n', 'CLAUDE.md': '- docs/10\n- docs/12 mine (ts3native 0.36.11)\n', 'shared.txt': 'one\nMINE\nthree\n',
    });
    g('add', '-A'); g('commit', '-qm', 'branch');
    g('checkout', '-q', 'main');
    put({
      'docs/12-theirs.md': '# theirs\n', [LEDGER]: '# L\n\n## 1. one\n\n## 2. two\n\n## 3. theirs\n',
      [REGISTRY]: reg(['| 0-3 | a | x |', '| 4-6 | them | z |'], ['| v1 | a | 0x0000-0x0010 | x |', '| v2 | them | 0x0014-0x0030 | z |']),
      [NATIVE_CPP]: '#define TS3NATIVE_VERSION "0.36.12"\n', 'shared.txt': 'one\nTHEIRS\nthree\n',
    });
    g('add', '-A'); g('commit', '-qm', 'base');
    const x = gather(dir, 'main', 'branch', new Map([[52, [99]]]));
    const f = analyze(x), has = (k: Kind, s: string) => f.some(i => i.problem && i.kind === k && i.text.includes(s));
    t(has('doc', 'docs/12-mine.md: 12 is taken on main by docs/12-theirs.md; next free: 53'), 'doc 12 clash, next free past the base max (51) skips 52 (open PR)');
    t(f.some(i => !i.problem && i.text.startsWith('docs/13-other.md')), 'doc 13 free');
    t(has('index', 'CLAUDE.md: an added line names docs/12; make it docs/53'), 'index line');
    t(has('ledger', '§3 "mine": main has §3 "theirs"; next free: §4'), 'ledger clash');
    t(has('registry', 'ts3fixes slots 4-5: main already uses them'), 'fixes slots');
    t(has('registry', 'ts3native counters v2: main already has it'), 'native version');
    t(has('version', 'TS3NATIVE_VERSION 0.36.11: main is already at 0.36.12; use 0.36.13'), 'version bump behind');
    t(has('version', 'CLAUDE.md names ts3native 0.36.11'), 'version named in docs');
    t(has('conflict', 'shared.txt') && has('conflict', LEDGER), 'merge-tree conflicts');
    t(x.headAdded.includes('docs/12-mine.md') && x.bothChanged.includes(NATIVE_CPP), 'gather lists');
    // contributor ranges: branch adds doc 12, ledger 3, slots 4-5, v2 and bumps the version: all outside; `clean` below stays free
    const fr = analyze({ ...x, ranges: true }), hr = (s: string) => fr.some(i => i.problem && i.kind === 'range' && i.text.includes(s));
    t(hr('docs/12-mine.md: 12 is outside the contributor range') && hr('next free: 500 ('), 'range: doc 12 outside, next free 500');
    t(hr('docs/51 §3 "mine"') && hr('next free: §900'), 'range: ledger 3 outside, next free 900');
    t(hr('ts3fixes slots 4-5') && hr('next free: 2000-2001'), 'range: slots 4-5 outside, next free 2000-2001');
    t(hr('ts3native counters v2') && hr('next free: v900'), 'range: counters v2 outside, next free v900');
    t(hr('TS3NATIVE_VERSION changed (0.36.10 -> 0.36.11): maintainers assign') && !fr.some(i => i.kind === 'version' && i.text.includes('is already at')), 'range: version change flagged, behind-base rule off');
    // same numbers inside the ranges: no range problem, and the next free skips what the base already uses in range
    g('checkout', '-qb', 'inrange', 'main');
    put({ 'docs/500-x.md': '# x\n', 'docs/501-y.md': '# y\n', [LEDGER]: '# L\n\n## 1. one\n\n## 2. two\n\n## 3. theirs\n\n## 900. c\n', [REGISTRY]: reg(['| 0-3 | a | x |', '| 4-6 | them | z |', '| 2000-2004 | c | c |'], ['| v1 | a | 0x0000-0x0010 | x |', '| v2 | them | 0x0014-0x0030 | z |', '| v900 | c | 0x0034-0x0040 | c |']) });
    g('add', '-A'); g('commit', '-qm', 'inrange');
    const ir = analyze({ ...gather(dir, 'main', 'inrange', new Map()), ranges: true });
    t(!ir.some(i => i.problem && i.kind === 'range'), 'range: numbers inside the contributor ranges are fine');
    g('checkout', '-qb', 'outrange', 'inrange'); put({ 'docs/20-z.md': '# z\n' }); g('add', '-A'); g('commit', '-qm', 'out');
    const orr = analyze({ ...gather(dir, 'main', 'outrange', new Map()), ranges: true });
    t(orr.some(i => i.problem && i.text.includes('docs/20-z.md: 20 is outside') && i.text.includes('next free: 502')), 'range: next free doc skips 500-501 the branch uses');
    // no clash: the branch rebased on main
    g('checkout', '-qb', 'clean', 'main'); put({ 'docs/13-ok.md': '# ok\n' }); g('add', '-A'); g('commit', '-qm', 'ok');
    const f2 = analyze(gather(dir, 'main', 'clean', new Map()));
    t(!f2.some(i => i.problem) && f2.some(i => i.text.startsWith('docs/13-ok.md: 13 is free')), 'clean branch has no problems');
    const f4 = analyze(gather(dir, 'main', 'clean', new Map([[13, [77]]])));
    t(f4.some(i => i.problem && i.text.includes('open PR #77 also adds docs/13')), 'open PR claim on the same number');
    // two new docs with one number in the same branch
    const f3 = analyze({ ...gather(dir, 'main', 'clean', new Map()), headAdded: ['docs/13-x.md', 'docs/13-y.md'] });
    t(f3.some(i => i.problem && i.text.includes("taken by this branch's docs/13-x.md")), 'same number twice in a branch');
  } finally { rmSync(dir, { recursive: true, force: true }); }
  console.log(`pr-preflight self-check: ${n} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (process.argv[1]?.endsWith('pr-preflight.ts')) {
  const { values: v } = parseArgs({ options: {
    base: { type: 'string', default: 'origin/main' }, pr: { type: 'string' }, repo: { type: 'string' }, all: { type: 'boolean' }, json: { type: 'boolean' },
    'no-checks': { type: 'boolean' }, 'no-prs': { type: 'boolean' }, maintainer: { type: 'boolean' }, 'self-check': { type: 'boolean' },
  } });
  if (v['self-check']) await selfCheck();
  else {
    const cwd = run('git', ['rev-parse', '--show-toplevel'], process.cwd()).trim();
    let headRef = 'HEAD', label = run('git', ['rev-parse', '--abbrev-ref', 'HEAD'], cwd).trim();
    const self = v.pr ? +v.pr : undefined;
    if (v.pr) {  // the PR head from the base's remote (works for forks: GitHub serves pull/<n>/head there)
      const remote = v.base!.includes('/') ? v.base!.split('/')[0] : 'origin';
      run('git', ['fetch', '-q', remote, `pull/${v.pr}/head`], cwd);
      headRef = run('git', ['rev-parse', 'FETCH_HEAD'], cwd).trim();
      label = `PR #${v.pr}`;
    }
    label += ` (${run('git', ['rev-parse', '--short', headRef], cwd).trim()})`;
    const baseDocs = tree(cwd, v.base!).docs;
    const claims = v['no-prs'] ? null : openClaims(cwd, v.repo, self, baseDocs);
    const x = { ...gather(cwd, v.base!, headRef, claims ?? new Map()), ranges: !v.maintainer };
    const f = analyze(x);
    if (!v['no-prs'] && !claims) f.push({ kind: 'note', problem: false, text: 'open PRs not checked (gh not available or not logged in)' });
    if (!v.pr && run('git', ['status', '--porcelain'], cwd).trim()) f.push({ kind: 'note', problem: false, text: 'uncommitted changes are not part of this check' });
    if (!v['no-checks']) {
      const changed = [...x.headDiff.keys()];
      let dir = cwd, tmp = '';
      if (headRef !== 'HEAD') { tmp = mkdtempSync(join(tmpdir(), 'pr-preflight-wt-')); run('git', ['worktree', 'add', '-q', '--detach', tmp, headRef], cwd); dir = tmp; }
      try { f.push(...checks(dir, changed)); } finally { if (tmp) run('git', ['worktree', 'remove', '--force', tmp], cwd); }
    }
    if (v.json) console.log(JSON.stringify({ head: label, base: x.baseName, conflicts: x.conflicts, bothChanged: x.bothChanged, findings: f }, null, 2));
    else report(label, x, f, !!v.all);
    if (f.some(i => i.problem)) process.exitCode = 1;
  }
}
