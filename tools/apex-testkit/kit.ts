// Apex test kit reader: the same pass/fail and counter numbers as our own runs, from a tester's logs.
// node kit.ts check --pr <N> <log>...         pass/fail regexes of expect.json; known non-failures listed, not failed
// node kit.ts counters <log> [Tag...]         "[Tag] ... key=number" lines: n, last, min, max per Tag.key; ts3native version
// node kit.ts timeline <csv> <col> [col=val]  Apex FrameTimeline.csv column: n, nonzero, mean, p99, max (optional row filter)
// node kit.ts hitch <csv> [col]               frame time series (default frame_ms): p50/p99/p99.9, max, counts > 25/50/100/250 ms
// node kit.ts sha <dir>                       sha256 of the pair in <dir> against SHA256SUMS.txt next to this script
// node kit.ts --self-check                    regex cases of expect.json + reader tests
// Node >= 22.18, no dependencies.
import { readFileSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const here = dirname(fileURLToPath(import.meta.url));
type Rx = { regex: string; why?: string; match?: string; nonMatch?: string; id?: string };
const expect = () => JSON.parse(readFileSync(join(here, 'expect.json'), 'utf8'));
const lines = (f: string) => readFileSync(f, 'utf8').split(/\r?\n/);

export function check(pr: any, known: Rx[], text: string[]) {
  const hits = (list: Rx[] = []) => list.map(r => ({ r, lines: text.filter(l => new RegExp(r.regex).test(l)) }));
  const knownHit = (l: string) => known.some(k => new RegExp(k.regex).test(l));
  const pass = hits(pr.pass), fail = hits(pr.fail).map(h => ({ ...h, lines: h.lines.filter(l => !knownHit(l)) }));
  const ok = pass.every(h => h.lines.length > 0) && fail.every(h => h.lines.length === 0);
  return { ok, pass, fail, known: hits(known) };
}

export function counters(text: string[], tags: string[]) {
  const out = new Map<string, number[]>();
  let version = '';
  for (const l of text) {
    const v = /release: (\S+)/.exec(l);
    if (v && !version) version = v[1];
    const t = /\[([A-Za-z0-9 _-]+)\]/.exec(l);
    if (!t || (tags.length && !tags.includes(t[1]))) continue;
    for (const m of l.slice(t.index).matchAll(/([A-Za-z_][\w.]*)=(-?\d+(?:\.\d+)?)/g)) {
      const k = `${t[1]}.${m[1]}`;
      (out.get(k) ?? out.set(k, []).get(k)!).push(Number(m[2]));
    }
  }
  return { version, rows: [...out].map(([k, v]) => ({ key: k, n: v.length, last: v[v.length - 1], min: Math.min(...v), max: Math.max(...v) })) };
}

function csv(text: string[]) {
  const rows = text.filter(l => l.trim()).map(l => l.split(','));
  return { head: rows[0].map(h => h.trim()), rows: rows.slice(1) };
}
const pct = (s: number[], p: number) => s.length ? s[Math.min(s.length - 1, Math.ceil(p * s.length) - 1)] : NaN;

export function column(text: string[], col: string, where?: string) {
  const c = csv(text), i = c.head.indexOf(col);
  if (i < 0) throw new Error(`no column ${col}; columns: ${c.head.join(' ')}`);
  let rows = c.rows;
  if (where) {
    const [wc, wv] = where.split('='), wi = c.head.indexOf(wc);
    if (wi < 0) throw new Error(`no column ${wc}`);
    rows = rows.filter(r => r[wi]?.trim() === wv);
  }
  const v = rows.map(r => Number(r[i])).filter(Number.isFinite), s = [...v].sort((a, b) => a - b);
  return { n: v.length, nonzero: v.filter(x => x !== 0).length, mean: v.length ? v.reduce((a, b) => a + b, 0) / v.length : NaN, p99: pct(s, 0.99), max: s.length ? s[s.length - 1] : NaN };
}

export function hitch(text: string[], col = 'frame_ms') {
  const c = csv(text), i = c.head.indexOf(col);
  if (i < 0) throw new Error(`no column ${col}`);
  const s = c.rows.map(r => Number(r[i])).filter(Number.isFinite).sort((a, b) => a - b);
  const gt = (t: number) => s.filter(x => x > t).length;
  return { frames: s.length, p50: pct(s, 0.5), p99: pct(s, 0.99), p999: pct(s, 0.999), max: s[s.length - 1], gt25: gt(25), gt50: gt(50), gt100: gt(100), gt250: gt(250) };
}

function selfCheck() {
  const e = expect();
  let n = 0;
  const rx = (r: Rx, where: string) => {
    assert.ok(r.match !== undefined && r.nonMatch !== undefined, `${where}: match and nonMatch needed`);
    assert.ok(new RegExp(r.regex).test(r.match!), `${where}: does not match its match line`);
    assert.ok(!new RegExp(r.regex).test(r.nonMatch!), `${where}: matches its nonMatch line`);
    n++;
  };
  (e.knownNotFailures ?? []).forEach((r: Rx) => rx(r, `known ${r.id}`));
  for (const [pr, p] of Object.entries<any>(e.prs)) for (const k of ['pass', 'fail']) (p[k] ?? []).forEach((r: Rx, j: number) => rx(r, `#${pr} ${k}[${j}]`));
  const r = check({ pass: [{ regex: 'ok=1' }], fail: [{ regex: '\\[ERROR\\]' }] }, [{ regex: 'Pattern not found' }],
    ['x ok=1', '[ERROR] [Transition routing] Pattern not found']);
  assert.equal(r.ok, true);
  assert.equal(check({ fail: [{ regex: '\\[ERROR\\]' }] }, [], ['[ERROR] boom']).ok, false);
  const c = counters(['release: 0.36.247', '[RouteGate] Sites=1 rgEntries=4', '[RouteGate] rgEntries=9', '[Other] a=1'], ['RouteGate']);
  assert.equal(c.version, '0.36.247');
  assert.deepEqual(c.rows.find(x => x.key === 'RouteGate.rgEntries'), { key: 'RouteGate.rgEntries', n: 2, last: 9, min: 4, max: 9 });
  const t = ['frame_ms,apex_moving,room_solve_waited', '10,0,0', '30,1,2', '12,1,0'];
  assert.deepEqual(column(t, 'room_solve_waited', 'apex_moving=1'), { n: 2, nonzero: 1, mean: 1, p99: 2, max: 2 });
  assert.equal(hitch(t).gt25, 1);
  assert.equal(hitch(t).max, 30);
  console.log(`self-check ok: ${n} expect.json regex cases, readers ok`);
}

const [cmd, ...a] = process.argv.slice(2);
if (cmd === '--self-check') selfCheck();
else if (cmd === 'check') {
  const pi = a.indexOf('--pr'), pr = a[pi + 1], files = a.filter((_, j) => j !== pi && j !== pi + 1);
  const e = expect(), p = e.prs[pr];
  if (!p || !files.length) { console.error(`usage: check --pr <N> <log>... (PRs: ${Object.keys(e.prs).join(' ')})`); process.exit(2); }
  const r = check(p, e.knownNotFailures ?? [], files.flatMap(lines));
  for (const h of r.pass) console.log(`${h.lines.length ? 'ok  ' : 'MISS'} pass /${h.r.regex}/ x${h.lines.length}${h.lines[0] ? ': ' + h.lines[0].slice(0, 160) : ''}`);
  for (const h of r.fail) console.log(`${h.lines.length ? 'FAIL' : 'ok  '} fail /${h.r.regex}/ x${h.lines.length}${h.lines[0] ? ': ' + h.lines[0].slice(0, 160) : ''}`);
  for (const h of r.known) if (h.lines.length) console.log(`note known non-failure ${h.r.id ?? h.r.regex} x${h.lines.length}`);
  for (const m of p.manual ?? []) console.log(`manual: ${m}`);
  console.log(r.ok ? `PASS #${pr} (log checks)` : `FAIL #${pr}`);
  process.exit(r.ok ? 0 : 1);
} else if (cmd === 'counters' && a[0]) {
  const r = counters(lines(a[0]), a.slice(1));
  console.log(`ts3native version: ${r.version || 'not found'}`);
  for (const x of r.rows) console.log(`${x.key} n=${x.n} last=${x.last} min=${x.min} max=${x.max}`);
} else if (cmd === 'timeline' && a[1]) console.log(JSON.stringify(column(lines(a[0]), a[1], a[2])));
else if (cmd === 'hitch' && a[0]) console.log(JSON.stringify(hitch(lines(a[0]), a[1])));
else if (cmd === 'sha' && a[0]) {
  const sums = join(here, 'SHA256SUMS.txt');
  if (!existsSync(sums)) { console.error('SHA256SUMS.txt missing'); process.exit(2); }
  let bad = 0;
  for (const [sha, name] of lines(sums).filter(l => l.trim()).map(l => l.trim().split(/\s+\*?/))) {
    const f = join(a[0], name);
    if (!existsSync(f)) { console.log(`skip ${name} (not in ${a[0]})`); continue; }
    const got = createHash('sha256').update(readFileSync(f)).digest('hex');
    console.log(`${got === sha ? 'ok  ' : 'BAD '} ${name}`);
    if (got !== sha) bad++;
  }
  process.exit(bad ? 1 : 0);
} else { console.error(readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').slice(1, 9).join('\n')); process.exit(2); }
