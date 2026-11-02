// Player / soak memory report (docs/191 "Player memory lines", docs/188 §7): reads one ts3native.log ([MemDiag] Light,
// ts3native 0.36.79+, release default on, or the full [MemDiag]) and prints, per metric:
//   - per load: the value at each world load (travel arrival included) and the change against the previous load
//   - over time: first -> last over the play samples, MB (or count) per hour, and a verdict
//       leak?   keeps growing: it still rises in the last third of the session and in most steps
//       cache   grew, then flat (a plateau: a cache filling to its size)
//       flat / falls
//   - destroyed-but-alive ("diag: memsus alive" lines): destroyed objects that survived 2+ GCs, by type, first -> last
//   - object counts by type per load ("diag: memsus types" lines)
//   - types tied to mods: "<assembly>/<class>" types matched to the script packages and assemblies the log lists
//   - type growth ("typegrowth:" lines, [MemDiag] TypeGrowth, docs/199) and GC size ("gcsize:" lines, [GC] SqrtHeapLimit)
//   - native call sites ("allocsites:" lines, [MemDiag] AllocSites, docs/201): totals, top sites by live MB, sites whose
//     live bytes grow from one <prefix>-empty (or any repeated) mark to the next (survive removal), sites still held at a
//     <prefix>-empty mark after the <prefix>-full one, regions splitting the largest free block, untracked allocations
//     (ts3native 0.36.86+); TS3W.exe addresses named only when docs/201 §13.2 named the exact site from its EA tag string
//     (nearest-symbol guesses were wrong for these rows, docs/201 §13), others stay bare VAs
// Reuses tools/mem-report.ts's line parser (diag: mem + memsus counts merged by sample number).
//   node tools/memory-report.ts <ts3native.log> [--json]
//   node tools/memory-report.ts --self-check
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';
import { parseLog, type Sample } from './mem-report.ts';

// metrics shown (MB unless counts); others in the line are kept in --json only
export const METRICS: { key: string; label: string; count?: boolean }[] = [
  { key: 'wCom', label: 'commit' }, { key: 'priv', label: 'private' }, { key: 'big', label: 'largest free' },
  { key: 'rc', label: 'resource cache' }, { key: 'gcHeap', label: 'Mono heap' }, { key: 'eaDir', label: 'EA direct blocks' },
  { key: 'eaCom', label: 'EA heap cores committed' }, { key: 'eaRes', label: 'EA heap cores reserved' },
  { key: 'rest', label: 'unattributed' },
  ...['cc', 'ppc', 'etRef', 'gsc', 'mapv', 'wq', 'wpl', 'ogs', 'smh'].map((key) => ({ key, label: `handlers ${key}`, count: true })),
  { key: 'alive', label: 'destroyed but alive', count: true },
];

type Alive = { at: number; seen: number; trk: number; freed: number; alive: number; hook: number; ms: number; top: Record<string, number> };
type Types = { at: number; n: number; kinds: number; ms: number; top: Record<string, number> };
// [MemDiag] TypeGrowth (docs/199): top entries name:KB:+dKB:rank:phases, "*" = Cork candidate
type TgCls = { name: string; kb: number; dkb: number; rank: number; phases: number; cand: boolean };
type TgPass = { n: number; gc: number; objs: number; mb: number; marked: number; classes: number; census: number; graph: number; top: TgCls[]; holders: Record<string, Record<string, number>> };
// [GC] SqrtHeapLimit (docs/199)
// [MemDiag] AllocSites (docs/201): one "allocsites:" sample
type AsRow = { kind: string; r: number; k: string; at: string; from: string; mb: number; n: number; dmb: number; dn: number; amb: number; fmb: number; since?: number };
type AsFree = { r: number; base: string; mb: number; below: string; above: string };
type AsUntr = { r: number; type: string; base: string; mb: number; com: number; prot: string; owner: string; first: number; isNew: boolean; win: string };
type AsSample = { n: number; tag: string; ev?: string; sum: Record<string, number>; rows: AsRow[]; free: AsFree[]; usum?: Record<string, string>; untr: AsUntr[] };
type GcSize = { gc: number; mode: number; live: number; limit: number; heap: number; alloc: number; speed: number; gcMs: number; grewMB: number; forced: number; wouldFree: number; walkMs: number };

const kv = (s: string) => Object.fromEntries([...s.matchAll(/(\w+)=(\S+)/g)].map((m) => [m[1], m[2]]));
const topOf = (s?: string) => Object.fromEntries((s && s !== '-' ? s.split(',') : []).map((e) => {
  const i = e.lastIndexOf(':');
  return [e.slice(0, i), Number(e.slice(i + 1))];
}));

export function readLog(text: string) {
  const { run, samples } = parseLog(text);
  const alive: Alive[] = [], types: Types[] = [], loads: string[] = [], tg: TgPass[] = [], gcsize: GcSize[] = [], as: AsSample[] = [];
  let packages: string[] = [], assemblies: string[] = [], lastN = 0;
  for (const line of text.split(/\r?\n/)) {
    const m = /^diag: mem #(\d+) /.exec(line);
    if (m) { lastN = Number(m[1]); continue; }
    let x = /^diag: memsus alive: (.*)$/.exec(line);
    if (x) {
      const v = kv(x[1]);
      alive.push({ at: lastN, seen: +v.seen, trk: +v.trk, freed: +v.freed, alive: +v.alive, hook: +v.hook, ms: +v.ms, top: topOf(v.top) });
      const s = samples.find((y) => y.n === lastN);
      if (s && s.v.alive === undefined) s.v.alive = +v.alive;
      continue;
    }
    x = /^diag: memsus types: (.*)$/.exec(line);
    if (x) { const v = kv(x[1]); types.push({ at: lastN, n: +v.n, kinds: +v.kinds, ms: +v.ms, top: topOf(v.top) }); continue; }
    x = /^typegrowth: #(\d+) (.*?) top=(.*)$/.exec(line);
    if (x) {
      const v = kv(x[2]);
      const top = (x[3] ? x[3].split(',') : []).map((e) => {
        const f = e.split(':'), cand = f[0].startsWith('*');
        return { name: cand ? f[0].slice(1) : f[0], kb: +f[1], dkb: +f[2], rank: +f[3], phases: +f[4], cand };
      });
      tg.push({ n: +x[1], gc: +v.gc, objs: +v.objs, mb: +v.MB, marked: +v.marked, classes: +v.classes, census: +v.census, graph: +v.graph, top, holders: {} });
      continue;
    }
    x = /^typegrowth: holders #(\d+) (\S+) KB=\S+ <-(.*)$/.exec(line);
    if (x) {
      const n = +x[1], pass = tg.find((t) => t.n === n);
      if (pass) pass.holders[x[2]] = topOf(x[3].trim().split(' ').filter(Boolean).join(','));
      continue;
    }
    x = /^allocsites: #(\d+) (\S+)(?: ev=(\S+))? (sum|live|grow|cyc|inbig|free|usum|untr) (.*)$/.exec(line);
    if (x) {
      const n = +x[1];
      let smp = as.find((a) => a.n === n);
      if (!smp) as.push(smp = { n, tag: x[2], ev: x[3], sum: {}, rows: [], free: [], untr: [] });
      if (x[4] === 'sum') smp.sum = Object.fromEntries(Object.entries(kv(x[5])).map(([k, v]) => [k, +v]));
      else if (x[4] === 'usum') smp.usum = kv(x[5]);
      else if (x[4] === 'untr') {
        const v = kv(x[5]);
        smp.untr.push({ r: +v.r, type: v.type, base: v.base, mb: +v.MB, com: +v.comMB, prot: v.prot, owner: v.owner, first: +v.first.slice(1), isNew: v.new === '1', win: v.win });
      }
      else if (x[4] === 'free') {
        const f = /r=(\d+) base=(\S+) MB=(\S+) below=(\{[^}]*\}|-) above=(\{[^}]*\}|-)/.exec(x[5]);
        if (f) smp.free.push({ r: +f[1], base: f[2], mb: +f[3], below: f[4], above: f[5] });
      } else {
        const v = kv(x[5]);
        smp.rows.push({ kind: x[4], r: +v.r, k: v.k, at: v.at, from: v.from, mb: +v.MB, n: +v.n, dmb: +(v.dMB ?? 0), dn: +(v.dN ?? 0),
          amb: +(v.aMB ?? 0), fmb: +(v.fMB ?? 0), ...(v.since ? { since: +v.since.slice(1) } : {}) });
      }
      continue;
    }
    x = /^gcsize: (.*)$/.exec(line);
    if (x) {
      const v = kv(x[1]);
      gcsize.push({ gc: +v.gc, mode: +v.mode, live: +v.live, limit: +v.limit, heap: +v.heap, alloc: +v.alloc, speed: +v.speed, gcMs: +v.gcMs,
        grewMB: +v.grewMB, forced: +v.forced, wouldFree: +v.wouldFree, walkMs: +v.walkMs });
      continue;
    }
    x = /^ts3fixes: world: ([^,]+),/.exec(line);
    if (x) { loads.push(x[1]); continue; }
    x = /^diag: script mods \(S3SA packages\): (.*)$/.exec(line);
    if (x) { packages = x[1].split(', ').map((p) => p.trim()); continue; }
    x = /^ts3fixes: script assemblies \(\d+\): (.*)$/.exec(line);
    if (x) assemblies = x[1].split(', ').map((a) => a.trim().split(/\s+/)[0]);
  }
  return { run, samples: samples.sort((a, b) => a.n - b.n), alive, types, loads, packages, assemblies, tg, gcsize, as: as.sort((a, b) => a.n - b.n) };
}

// leak? / cache / flat / falls over one series (values in time order); thr = change that matters (MB or count)
export function verdict(vals: number[], thr: number): string {
  if (vals.length < 3) return 'too few samples';
  const first = vals[0], last = vals[vals.length - 1], g = last - first;
  if (Math.abs(g) < thr) return 'flat';
  if (g < 0) return 'falls';
  const third = vals[Math.floor((vals.length - 1) * 2 / 3)], late = last - third;
  let ups = 0;
  for (let i = 1; i < vals.length; i++) if (vals[i] - vals[i - 1] > 0) ups++;
  return late >= Math.max(thr / 2, g / 4) && ups >= (vals.length - 1) * 0.6 ? 'leak? (keeps growing)' : 'cache (plateau)';
}

const norm = (s: string) => s.toLowerCase().replace(/\.package$/, '').replace(/[^a-z0-9]/g, '');
// "<assembly>/<class>" -> the script package that most likely holds the assembly (name containment, either way)
export function modOf(type: string, packages: string[]): string {
  const i = type.indexOf('/');
  if (i < 0) return 'EA';
  const asm = norm(type.slice(0, i));
  const p = packages.find((x) => { const n = norm(x); return n.includes(asm) || (asm.length > 4 && asm.includes(n)); })
    ?? packages.find((x) => { const n = norm(x); return asm.length > 4 && n.length > 4 && (n.includes(asm.slice(0, 5)) || asm.includes(n.slice(0, 5))); });
  return p ? `${type.slice(0, i)} (${p})` : `${type.slice(0, i)} (package not listed)`;
}

const fmt = (x: number) => (Math.abs(x) >= 100 ? x.toFixed(0) : x.toFixed(1));

// TS3W.exe return addresses named in docs/201 §12-13.2 from the EA allocation tag string in the calling function or the
// code itself [verified static]; every other address stays a bare VA (the nearest-symbol guess was wrong for these rows)
export const SITES: Record<string, string> = {
  '0x004DEC17': 'EAStreamMemory/data', '0x004DEE8C': 'EAStreamMemory/ptr',
  '0x0072D62B': 'PackedStream/DecompressedRecordData', '0x0072D6C9': 'PackedStream/DecompressedRecordData',
  '0x0057A593': 'Animation/ClipResource', '0x00578F6D': 'Animation/ClipResource vector',
  '0x00D71558': 'Scripting/Simulator sub-heap core', '0x00D70A22': 'Mono runtime malloc',
  '0x005C7906': 'CAS/GeometryResource', '0x005A2FFF': 'CAS/BlendGeometryResource/BlendRegionData',
  '0x008B452D': 'Audio/ResourceBinary/Data', '0x008B6022': 'Audio/ResourceBinarySampleData',
  '0x0060D91C': 'Renderer/Texture', '0x00D70916': 'Boehm GC section',
  '0x004E4E87': 'EA heap new core', '0x004E5130': 'EA heap direct block',
};
// GPU driver modules (32-bit Vulkan ICDs under DXVK): their reserves (nvoglv32 128 MB) are accepted DXVK cost, labelled
// `driver` and not investigated further (docs/201 §14); ts3native 0.36.86 keeps them out of miRes
export const DRIVERS = /^(nvoglv32|amdvlk32|igvk32)\.dll\+/i;
export function nameOf(at: string): string {
  if (DRIVERS.test(at)) return `${at} (driver)`;
  const k = /^0x[0-9A-Fa-f]{8}$/.test(at) ? '0x' + at.slice(2).toUpperCase() : '';
  return SITES[k] ? `${at} (${SITES[k]})` : at;
}
const siteKey = (x: AsRow) => `${x.k} ${x.at} < ${x.from}`;
const siteName = (x: AsRow) => `${x.k} ${nameOf(x.at)} < ${nameOf(x.from)}`;
const nameIn = (s: string) => s.replace(/=(0x[0-9A-Fa-f]{8}|[\w.-]+\.dll\+0x[0-9A-Fa-f]+)/g, (_m, a) => `=${nameOf(a)}`);

// [MemDiag] AllocSites section (docs/201)
export function allocSitesReport(as: AsSample[]): string[] {
  const out: string[] = [];
  if (!as.length) return out;
  const f = as[0], l = as[as.length - 1], g = (s: AsSample, k: string) => s.sum[k] ?? NaN;
  out.push('', `native call sites (allocsites, docs/201, 1 in ${g(l, 'sample')} heap blocks < 64 KB): ${as.length} samples; heap ${fmt(g(f, 'heapMB'))} -> ${fmt(g(l, 'heapMB'))} MB, ` +
    `virtual reserves ${fmt(g(f, 'virtMB'))} -> ${fmt(g(l, 'virtMB'))} MB, views ${fmt(g(f, 'mapMB'))} -> ${fmt(g(l, 'mapMB'))} MB; last: sites ${g(l, 'sites')}, ` +
    `drops ${g(l, 'drops')}, busy ${g(l, 'busy')}, frees not found ${g(l, 'nofree')}, releases of regions made before the hooks ${g(l, 'rgnMiss')}, ` +
    `virtual hook ${fmt(g(l, 'vUs') / Math.max(1, g(l, 'vCalls')))} us avg (max ${g(l, 'vMaxUs')})`);
  const last = (k: string) => [...as].reverse().find((s) => (k === 'free' ? s.free.length : s.rows.some((y) => y.kind === k))) ?? l;
  const lv = last('live');
  out.push(`  top live at #${lv.n} ${lv.tag}${lv.ev ? ` ${lv.ev}` : ''}:`);
  for (const x of lv.rows.filter((y) => y.kind === 'live').slice(0, 12)) out.push(`    ${fmt(x.mb)} MB n=${x.n}: ${siteName(x)}`);
  // repeated tags: growth from one cycle's mark to the next one with the same tag (native "cyc" rows)
  const tags = [...new Set(as.filter((s) => s.ev && s.rows.some((y) => y.kind === 'cyc')).map((s) => s.ev!))];
  for (const t of tags) {
    const smps = as.filter((s) => s.ev === t), cycs = smps.filter((s) => s.rows.some((y) => y.kind === 'cyc'));
    const per = new Map<string, { x: AsRow; d: number[] }>();
    cycs.forEach((s, i) => {
      for (const y of s.rows.filter((z) => z.kind === 'cyc')) {
        const e = per.get(siteKey(y)) ?? { x: y, d: Array(cycs.length).fill(0) };
        e.d[i] += y.dmb, e.x = y;
        per.set(siteKey(y), e);
      }
    });
    const rows = [...per.values()].map((e) => ({ ...e, sum: e.d.reduce((a, b) => a + b, 0), every: e.d.every((v) => v > 0.25) })).sort((a, b) => b.sum - a.sum);
    const empty = /-empty$|gc-after|home/.test(t);
    out.push(`  mark ${t} x${smps.length}: growth to the next ${t} mark (${cycs.length} steps)${empty ? '; growth here survives removal' : ''}:`);
    for (const e of rows.slice(0, 10))
      out.push(`    ${e.every && empty ? 'SURVIVES ' : ''}${e.d.map((v) => `${v >= 0 ? '+' : ''}${v.toFixed(2)}`).join(' ')} MB (sum ${fmt(e.sum)}, live ${fmt(e.x.mb)}): ${siteName(e.x)}`);
  }
  // <prefix>-full -> the next <prefix>-empty: top live sites that kept >= 90 % through the removal
  for (const full of as.filter((s) => s.ev?.endsWith('-full'))) {
    const pre = full.ev!.slice(0, -5), emp = as.find((s) => s.n > full.n && s.ev === `${pre}-empty`);
    if (!emp) continue;
    const held = full.rows.filter((y) => y.kind === 'live').map((y) => ({ y, e: emp.rows.find((z) => z.kind === 'live' && siteKey(z) === siteKey(y)) }))
      .filter((p) => p.e && p.e.mb >= p.y.mb * 0.9 && p.y.mb >= 1);
    if (held.length) out.push(`  held through ${pre} removal (#${full.n} -> #${emp.n}): ${held.slice(0, 6).map((p) => `${siteName(p.y)} ${fmt(p.y.mb)} -> ${fmt(p.e!.mb)} MB`).join('; ')}`);
  }
  const inb = last('inbig').rows.filter((y) => y.kind === 'inbig');
  if (inb.length) out.push(`  regions inside the previous sample's largest free block (they split it): ${inb.map((y) => `${siteName(y)} ${fmt(y.mb)} MB x${y.n}`).join('; ')}`);
  for (const fr of last('free').free) out.push(`  free #${fr.r} ${fr.base} ${fmt(fr.mb)} MB: below ${nameIn(fr.below)}, above ${nameIn(fr.above)}`);
  // untracked allocations >= 1 MB (0.36.86+): totals of the last sample, then every allocation that showed up after the
  // first sample or sat inside / at the largest free block (by base + size, first sighting)
  const us = as.filter((s) => s.usum);
  if (us.length) {
    const lu = us[us.length - 1], u = lu.usum!;
    out.push(`  untracked allocations >= 1 MB at #${lu.n}: private ${u.private}, map ${u.map}, image ${u.image} (count/MB)${+u.more ? `, ${u.more} not listed` : ''}`);
    const seen = new Map<string, { s: AsSample; x: AsUntr }>();
    for (const s of us)
      for (const x of s.untr)
        if ((x.isNew || x.win !== '-') && !seen.has(`${x.base}+${x.mb}`)) seen.set(`${x.base}+${x.mb}`, { s, x });
    for (const { s, x } of seen.values())
      out.push(`    #${s.n} ${s.tag}${s.ev ? ` ${s.ev}` : ''}: ${x.type} ${x.base} ${fmt(x.mb)} MB (commit ${fmt(x.com)}, prot ${x.prot}) owner ${x.owner}` +
        `${x.isNew ? ', new' : ''}${x.win === 'in' ? ', inside the previous largest free block' : x.win === 'edge' ? ', at the largest free block' : ''}`);
    const other = lu.untr.filter((x) => !x.isNew && x.win === '-').sort((a, b) => b.mb - a.mb).slice(0, 8);
    if (other.length) out.push(`    largest other listed at #${lu.n}: ${other.map((x) => `${x.type} ${x.base} ${fmt(x.mb)} MB ${x.owner} since #${x.first}`).join('; ')}`);
  }
  return out;
}

export function report(text: string): string {
  const r = readLog(text), out: string[] = [];
  out.push(`run ${r.run ?? '(no ts3native header)'}: ${r.samples.length} memory samples, ${r.loads.length} world loads (${r.loads.join(' -> ') || '-'}), ${r.alive.length} alive lines`);
  if (!r.samples.length) { out.push('no "diag: mem" lines: [MemDiag] Light off or ts3native older than 0.36.69'); return out.join('\n'); }
  const loadS = r.samples.filter((s) => s.tag === 'load'), play = r.samples.filter((s) => s.tag !== 'selftest');  // marks and saves too: a short session may have no periodic sample
  out.push('', 'metric | per load (value, change vs previous load) | over time first -> last, per hour | verdict');
  for (const m of METRICS) {
    const pts = play.filter((s) => typeof s.v[m.key] === 'number');
    if (!pts.length) continue;
    const vals = pts.map((s) => s.v[m.key] as number);
    const per = loadS.filter((s) => typeof s.v[m.key] === 'number').map((s, i, a) => {
      const v = s.v[m.key] as number, p = i ? a[i - 1].v[m.key] as number : null;
      return `${s.ev ? `[${s.ev}] ` : ''}${fmt(v)}${p === null ? '' : ` (${v - p >= 0 ? '+' : ''}${fmt(v - p)})`}`;
    }).join(', ');
    const a = pts[0], b = pts[pts.length - 1], dt = a.t !== undefined && b.t !== undefined ? (b.t - a.t) / 3600 : 0;
    const ph = dt > 0 ? `, ${(vals[vals.length - 1] - vals[0]) / dt >= 0 ? '+' : ''}${fmt((vals[vals.length - 1] - vals[0]) / dt)}/h` : '';
    // largest free shrinking is the bad direction: judge its negation
    const v = verdict(m.key === 'big' ? vals.map((x) => -x) : vals, m.count ? 2 : 8);
    out.push(`${m.label} (${m.key}${m.count ? '' : ', MB'}) | ${per || '-'} | ${fmt(vals[0])} -> ${fmt(vals[vals.length - 1])}${ph} | ${m.key === 'big' ? v.replace('growing', 'shrinking') : v}`);
  }
  // docs/209 #96: under DXVK most texture data sits outside TS3W's address space, so rc moves far more than free space does
  if (play.some((s) => typeof s.v.rc === 'number')) out.push('note: rc (resource cache) is not 32-bit address space under DXVK (docs/40 §4-5, docs/209 #96); judge Error 12 room by big and free.');
  if (r.alive.length) {
    const f = r.alive[0], l = r.alive[r.alive.length - 1];
    out.push('', `destroyed but alive (1 in 32 destroys tracked, alive = survived 2+ GCs): hook ${l.hook ? 'on' : 'NOT applied'}, destroys seen ${l.seen}, tracked ${l.trk}, freed ${l.freed}, alive ${f.alive} -> ${l.alive} (${verdict(r.alive.map((x) => x.alive), 2)}), read ${Math.max(...r.alive.map((x) => x.ms)).toFixed(2)} ms max`);
    const keys = [...new Set(r.alive.flatMap((x) => Object.keys(x.top)))];
    const rows = keys.map((k) => ({ k, first: f.top[k] ?? 0, last: l.top[k] ?? 0, max: Math.max(...r.alive.map((x) => x.top[k] ?? 0)), series: r.alive.map((x) => x.top[k] ?? 0) }))
      .sort((x, y) => y.last - x.last || y.max - x.max);
    for (const x of rows.slice(0, 12)) out.push(`  ${x.k}: ${x.first} -> ${x.last} (max ${x.max}) ${verdict(x.series, 2)}; ${modOf(x.k, r.packages)}`);
  }
  for (const t of r.types) {
    out.push('', `object types after load (sample #${t.at}): ${t.n} objects, ${t.kinds} types, read ${t.ms.toFixed(1)} ms`);
    for (const [k, n] of Object.entries(t.top)) out.push(`  ${k}: ${n}; ${modOf(k, r.packages)}`);
  }
  if (r.tg.length) {
    const ms = r.tg.map((t) => t.census + t.graph), l = r.tg[r.tg.length - 1];
    out.push('', `type growth (Cork, docs/199): ${r.tg.length} passes, pass ms avg ${fmt(ms.reduce((a, b) => a + b, 0) / ms.length)} max ${fmt(Math.max(...ms))} (census max ${fmt(Math.max(...r.tg.map((t) => t.census)))}, graph max ${fmt(Math.max(...r.tg.map((t) => t.graph)))}); live managed ${fmt(r.tg[0].mb)} -> ${fmt(l.mb)} MB, objects ${r.tg[0].objs} -> ${l.objs}, classes ${l.classes}`);
    const cands = [...new Set(r.tg.flatMap((t) => t.top.filter((c) => c.cand).map((c) => c.name)))];
    out.push(`  candidates (phases >= 2, rank > threshold, any pass): ${cands.length ? cands.join(', ') : 'none'}`);
    for (const c of l.top) {
      const first = r.tg.flatMap((t) => t.top).find((y) => y.name === c.name)!;
      const h = l.holders[c.name];
      out.push(`  ${c.cand ? '*' : ' '}${c.name}: ${fmt(first.kb)} -> ${fmt(c.kb)} KB, rank ${c.rank.toFixed(1)}, phases ${c.phases}${h ? `; held by ${Object.entries(h).slice(0, 4).map(([k, v]) => `${k} ${fmt(v)} KB`).join(', ')}` : ''}`);
    }
  }
  if (r.gcsize.length) {
    const g = r.gcsize, f = g[0], l = g[g.length - 1], sum = (k: keyof GcSize) => g.reduce((a, b) => a + b[k], 0);
    out.push('', `GC size (square-root rule, docs/199, mode ${[...new Set(g.map((x) => x.mode))].join('/')}): ${g.length} lines; live ${fmt(f.live)} -> ${fmt(l.live)} MB, limit ${fmt(f.limit)} -> ${fmt(l.limit)}, heap ${fmt(f.heap)} -> ${fmt(l.heap)} (max ${fmt(Math.max(...g.map((x) => x.heap)))}); alloc ${fmt(sum('alloc') / g.length)} MB/s, speed ${fmt(sum('speed') / g.length)} MB/s avg; grown by the rule ${fmt(sum('grewMB'))} MB, forced (collect_or_expand to the game) ${sum('forced')}, GC ms/cycle avg ${fmt(sum('gcMs') / g.length)}, walk ms max ${fmt(Math.max(...g.map((x) => x.walkMs)))}, would free (above the limit) last ${fmt(l.wouldFree)} MB`);
  }
  out.push(...allocSitesReport(r.as));
  const mods = [...new Set([...r.alive.flatMap((x) => Object.keys(x.top)), ...r.types.flatMap((x) => Object.keys(x.top))].filter((k) => k.includes('/')))];
  out.push('', `mod types seen: ${mods.length ? mods.map((k) => modOf(k, r.packages)).filter((v, i, a) => a.indexOf(v) === i).join('; ') : 'none (EA types only)'}`);
  if (r.assemblies.length) out.push(`script assemblies in the log: ${r.assemblies.join(', ')}`);
  return out.join('\n');
}

// --steady <prefix> (docs/200 §11): the same add / remove cycle repeated; per cycle the <prefix>-full and <prefix>-empty marks
// (values from the gc-after sample that follows each mark when there is one) and the step vs the previous cycle.
// Verdict per metric over the steps from cycle 2 on (cycle 1 -> 2 may fill caches): leak = a steady step in the bad
// direction, settling = steps shrinking to ~0, flat = no step >= 2 MB.
const STEADY: { key: string; up: boolean }[] = [  // up = growth is the bad direction (free and largest free: shrinking is)
  { key: 'free', up: false }, { key: 'big', up: false }, { key: 'priv', up: true }, { key: 'map', up: true }, { key: 'eaDir', up: true }, { key: 'miCom', up: true }];
export function steadyVerdict(steps: number[], thr = 2): string {  // steps in the bad direction (+ = worse), cycle 1->2 first
  const late = steps.slice(1);
  if (late.length < 2) return 'too few cycles';
  const med = [...late].sort((a, b) => a - b)[Math.floor(late.length / 2)], last = late[late.length - 1];
  if (late.every((x) => Math.abs(x) < thr)) return steps[0] >= thr ? `flat from cycle 2 (fill ${fmt(steps[0])} in cycle 1->2)` : 'flat';
  if (med >= thr && last >= thr / 2) return `leak (~${fmt(med)} MB per cycle from cycle 2)`;
  if (Math.abs(last) < thr && Math.abs(last) <= Math.abs(late[0])) return 'settling (steps shrink to ~0)';
  return 'mixed';
}
export function steadyReport(text: string, prefix: string): string {
  const r = readLog(text), out: string[] = [];
  const at = (ev: string) => r.samples.filter((s) => s.ev === ev).map((s) => {
    const nx = r.samples.find((y) => y.n > s.n && y.ev && y.ev !== 'gc-before');  // the next mark after this one
    return nx?.ev === 'gc-after' ? { s: nx, gc: true } : { s, gc: false };
  });
  const full = at(`${prefix}-full`), empty = at(`${prefix}-empty`), n = Math.max(full.length, empty.length);
  out.push(`steady state ${prefix} (docs/200 §11): ${full.length} x ${prefix}-full, ${empty.length} x ${prefix}-empty; values after the forced GC where logged; step = vs the previous cycle (MB)`);
  if (!n) return out.concat(`no ${prefix}-full / ${prefix}-empty marks`).join('\n');
  const val = (p: { s: Sample } | undefined, k: string) => (p && typeof p.s.v[k] === 'number' ? p.s.v[k] as number : NaN);
  const cell = (ps: typeof full, i: number, k: string) => {
    const v = val(ps[i], k), d = i ? v - val(ps[i - 1], k) : NaN;
    return Number.isNaN(v) ? '-' : `${fmt(v)}${Number.isNaN(d) ? '' : ` (${d >= 0 ? '+' : ''}${fmt(d)})`}`;
  };
  for (const [name, ps] of [['full', full], ['empty', empty]] as const) {
    out.push('', `${prefix}-${name}: cycle | ${STEADY.map((m) => m.key).join(' | ')}`);
    for (let i = 0; i < ps.length; i++) out.push(`  ${i + 1}${ps[i].gc ? '' : ' (no gc-after)'} | ${STEADY.map((m) => cell(ps, i, m.key)).join(' | ')}`);
    for (const m of STEADY) {
      const v = ps.map((p) => val(p, m.key));
      if (v.some(Number.isNaN)) continue;
      const steps = v.slice(1).map((x, i) => (m.up ? x - v[i] : v[i] - x));
      out.push(`  ${m.key}: ${steadyVerdict(steps)}`);
    }
  }
  // allocsites: sites whose live MB grows full -> full (and empty -> empty) in every step from cycle 2 on
  for (const ev of [`${prefix}-full`, `${prefix}-empty`]) {
    const cycs = r.as.filter((s) => s.ev === ev && s.rows.some((y) => y.kind === 'cyc'));
    if (cycs.length < 2) continue;
    const per = new Map<string, { x: AsRow; d: number[] }>();
    cycs.forEach((s, i) => {
      for (const y of s.rows.filter((z) => z.kind === 'cyc')) {
        const e = per.get(siteKey(y)) ?? { x: y, d: Array(cycs.length).fill(0) };
        e.d[i] += y.dmb, e.x = y;
        per.set(siteKey(y), e);
      }
    });
    const grow = [...per.values()].filter((e) => e.d.slice(1).every((v) => v > 0.25)).sort((a, b) => b.d.reduce((s, v) => s + v, 0) - a.d.reduce((s, v) => s + v, 0));
    out.push('', `allocsites growing ${ev} -> ${ev} in every step from cycle 2 on (${cycs.length} steps): ${grow.length ? '' : 'none'}`);
    for (const e of grow.slice(0, 10)) out.push(`  ${e.d.map((v) => `${v >= 0 ? '+' : ''}${v.toFixed(2)}`).join(' ')} MB (live ${fmt(e.x.mb)}): ${siteName(e.x)}`);
  }
  return out.join('\n');
}

function selfCheck() {
  assert.equal(verdict([100, 110, 120, 130, 140, 150], 8), 'leak? (keeps growing)');
  assert.equal(verdict([100, 160, 200, 201, 200, 202], 8), 'cache (plateau)');
  assert.equal(verdict([100, 101, 100, 102], 8), 'flat');
  assert.equal(verdict([100, 80, 60], 8), 'falls');
  assert.equal(verdict([1, 2], 8), 'too few samples');
  const pk = ['NRaas_Overwatch.package', 'ts3fixes.package', 'simler90GameplayCoreMod-UPDATE242-Steam.package'];
  assert.equal(modOf('Sim', pk), 'EA');
  assert.equal(modOf('NRaasOverwatch/StuckCheck', pk), 'NRaasOverwatch (NRaas_Overwatch.package)');
  assert.equal(modOf('Ts3Fixes/X', pk), 'Ts3Fixes (ts3fixes.package)');
  assert.match(modOf('simler90Extras/Y', pk), /simler90GameplayCoreMod/);
  assert.match(modOf('Unknown/Z', pk), /package not listed/);
  const mem = (n: number, tag: string, ev: string, priv: number, rc: number, t: number) =>
    `diag: mem #${n} ${tag}${ev ? ` ev=${ev}` : ''} priv=${priv} wPriv=${priv - 40} wCom=${priv + 500} free=1600 big=${3000 - priv}? img=400? map=100 stkRes=98 stkCom=10.0? miRes=192 miCom=60 eaDir=160? eaDirN=110? eaCom=600? eaRes=640? eaCoreN=40? rp=0.0 pl=0.0? sp=0.0? gcHeap=350? gcFree=70? gcN=359? rc=${rc}? rcCold=30? rcBudget=200? rcN=1000? anim=4.0? rest=900? t=${t}`;
  const sus = (n: number, tag: string, wq: number) => `diag: memsus #${n} ${tag} cc=100 ccIcon=0? ppc=3 etRef=1 gsc=5 mapv=40 wq=${wq} wpl=2 ogs=5 smh=0? gcCnt=${380 + n} rdMs=0.25 lag=100 t=${n * 300}`;
  const alive = (k: number) => `diag: memsus alive: seen=${k * 300} trk=${k * 9} freed=${k * 5} alive=${k * 2} hook=1 ms=0.40 top=Ingredient:${k},NRaasOverwatch/Thing:${k - 1},Plate:1`;
  const lines = ['ts3native 0.36.79 2026-10-26 09:00:00',
    'diag: script mods (S3SA packages): NRaas_Overwatch.package, ts3fixes.package',
    'ts3fixes: script assemblies (2): NRaasOverwatch  3f0d4148, Ts3Fixes  a8848799',
    'ts3fixes: world: Bridgeport.world, lots 83 (residential 46), objects 10837'];
  for (let i = 1; i <= 7; i++) {
    lines.push(mem(i, i === 1 ? 'load' : 'periodic', '', 1500 + i * 20, i < 4 ? 170 + i * 60 : 360, i * 300), sus(i, i === 1 ? 'load' : 'periodic', 8 + i), alive(i));
    if (i === 1) lines.push('diag: memsus types: n=10837 kinds=300 ms=12.50 top=Sim:265,Plate:400,NRaasOverwatch/Thing:3');
  }
  const r = readLog(lines.join('\r\n'));
  assert.equal(r.samples.length, 7);
  assert.equal(r.samples[2].v.wq, 11, 'memsus merged by sample number');
  assert.equal(r.samples[6].v.alive, 14, 'alive merged into its sample');
  assert.equal(r.alive.length, 7);
  assert.deepEqual(r.types[0].top, { Sim: 265, Plate: 400, 'NRaasOverwatch/Thing': 3 });
  assert.deepEqual(r.loads, ['Bridgeport.world']);
  const txt = report(lines.join('\n'));
  assert.match(txt, /private \(priv, MB\) .*\| leak\? \(keeps growing\)/);
  assert.match(txt, /resource cache \(rc, MB\) .*\| cache \(plateau\)/);
  assert.match(txt, /EA heap cores committed \(eaCom, MB\) \| /);
  assert.match(txt, /note: rc \(resource cache\) is not 32-bit address space under DXVK/);
  assert.match(txt, /largest free \(big, MB\) .*leak\? \(keeps shrinking\)/);
  assert.match(txt, /handlers wq .*\| leak\? \(keeps growing\)/);
  assert.match(txt, /handlers ppc .*\| flat/);
  assert.match(txt, /destroyed but alive .*alive 2 -> 14/);
  assert.match(txt, /NRaasOverwatch\/Thing: 0 -> 6 .*NRaas_Overwatch\.package/);
  assert.match(txt, /object types after load .*10837 objects/);
  assert.match(report('ts3native 0.36.53 x\nnothing'), /no "diag: mem" lines/);
  const tgl = [...lines,
    'typegrowth: #1 gc=40 objs=900000 MB=150.0 marked=200.0 classes=4000 census=60.0 graph=90.0 top=Sims3.Gameplay.Foo:100:+0:0.0:1,System.String:5000:+0:0.0:1',
    'typegrowth: #2 gc=41 objs=910000 MB=152.0 marked=202.0 classes=4001 census=62.0 graph=95.0 top=*Sims3.Gameplay.Foo:200:+100:150.0:3,System.String:5000:+0:0.0:2',
    'typegrowth: holders #2 Sims3.Gameplay.Foo KB=200 <- Sims3.Gameplay.Foo[]:180 Sims3.Gameplay.Bar:20 other:4',
    'gcsize: gc=40 mode=1 live=200.0 limit=380.0 heap=352.0 alloc=8.00 speed=900 gcMs=120.0 grewMB=0.0 forced=3 wouldFree=0.0 walkMs=2.10',
    'gcsize: gc=50 mode=1 live=210.0 limit=390.0 heap=384.0 alloc=9.00 speed=950 gcMs=100.0 grewMB=32.0 forced=1 wouldFree=0.0 walkMs=2.30'];
  const rt = readLog(tgl.join('\n'));
  assert.equal(rt.tg.length, 2);
  assert.deepEqual(rt.tg[1].top[0], { name: 'Sims3.Gameplay.Foo', kb: 200, dkb: 100, rank: 150, phases: 3, cand: true });
  assert.deepEqual(rt.tg[1].holders['Sims3.Gameplay.Foo'], { 'Sims3.Gameplay.Foo[]': 180, 'Sims3.Gameplay.Bar': 20, other: 4 });
  assert.equal(rt.gcsize.length, 2);
  assert.equal(rt.gcsize[1].grewMB, 32);
  const tt = report(tgl.join('\n'));
  assert.match(tt, /type growth \(Cork, docs\/199\): 2 passes, pass ms avg 154 max 157/);
  assert.match(tt, /candidates .*: Sims3\.Gameplay\.Foo/);
  assert.match(tt, /\*Sims3\.Gameplay\.Foo: 100 -> 200 KB, rank 150\.0, phases 3; held by Sims3\.Gameplay\.Foo\[\] 180 KB/);
  assert.match(tt, /GC size .*heap 352 -> 384 .*grown by the rule 32\.0 MB, forced \(collect_or_expand to the game\) 4/);
  // allocsites (docs/201): parse, names, survive-removal across empty marks, held through removal, free neighbours
  assert.equal(nameOf('0x004E4931'), '0x004E4931');  // no guess: only docs/201 §13.2 sites get a name
  assert.equal(nameOf('0x004DEC17'), '0x004DEC17 (EAStreamMemory/data)');
  assert.equal(nameOf('0x004e4e87'), '0x004e4e87 (EA heap new core)');
  assert.equal(nameOf('d3d9.dll+0x1234'), 'd3d9.dll+0x1234');
  assert.equal(nameOf('nvoglv32.dll+0x8ACB7E'), 'nvoglv32.dll+0x8ACB7E (driver)');
  assert.equal(nameIn('{k=virtual at=nvoglv32.dll+0x8ACB7E from=-}'), '{k=virtual at=nvoglv32.dll+0x8ACB7E (driver) from=-}');
  const asl = (n: number, ev: string, rows: string[]) => [`allocsites: #${n} mark ev=${ev} sum heapMB=100.0 heapN=5000 virtMB=900.0 virtN=80 mapMB=120.0 mapN=12 sites=300 blocks=4000 regions=92 sampled=9000 drops=0 busy=0 nofree=3 rgnMiss=1 vCalls=500 vUs=2000 vMaxUs=90 sample=64`,
    ...rows.map((x) => `allocsites: #${n} mark ev=${ev} ${x}`)];
  const al = [...lines,
    ...asl(10, 'live-full', ['live r=1 k=heap at=0x004E4931 from=0x00600000 MB=40.00 n=640 dMB=+40.00 dN=+640 aMB=50.0 fMB=10.0', 'live r=2 k=map at=d3d9.dll+0x1234 from=- MB=32.00 n=2 dMB=+0.00 dN=+0 aMB=32.0 fMB=0.0']),
    ...asl(11, 'live-empty', ['live r=1 k=heap at=0x004E4931 from=0x00600000 MB=39.00 n=620 dMB=-1.00 dN=-20 aMB=50.0 fMB=11.0', 'live r=2 k=map at=d3d9.dll+0x1234 from=- MB=4.00 n=1 dMB=-28.00 dN=-1 aMB=32.0 fMB=28.0']),
    ...asl(12, 'live-empty', ['live r=1 k=heap at=0x004E4931 from=0x00600000 MB=42.00 n=660 dMB=+3.00 dN=+40 aMB=60.0 fMB=18.0',
      'cyc r=1 k=heap at=0x004E4931 from=0x00600000 MB=42.00 n=660 dMB=+3.00 dN=+40 aMB=60.0 fMB=18.0 since=#11',
      'cyc r=2 k=virtual at=0x00D7092A from=0x00E4EF00 MB=32.00 n=1 dMB=+0.10 dN=+0 aMB=64.0 fMB=32.0 since=#11',
      'inbig r=1 k=virtual at=0x00D7092A from=0x00E4EF00 MB=32.00 n=1 win=0x9EFC0000+1211MB',
      'free r=1 base=0x9F000000 MB=1211.0 below={k=virtual at=0x00D7092A from=0x00E4EF00} above={image ntdll.dll+0x1000}',
      'usum private=40/306.0MB map=3/34.0MB image=50/400.0MB new=1 more=0',
      'untr r=1 type=private base=0x10000000 MB=8.00 comMB=8.00 prot=0x4 aprot=0x4 owner=ts3native-allocsites first=#10 new=0 win=-',
      'untr r=2 type=private base=0x9EFC0000 MB=16.00 comMB=16.00 prot=0x4 aprot=0x4 owner=? first=#12 new=1 win=in']),
    ...asl(13, 'live-empty', ['cyc r=1 k=heap at=0x004E4931 from=0x00600000 MB=44.50 n=700 dMB=+2.50 dN=+40 aMB=70.0 fMB=25.5 since=#12'])];
  const ra = readLog(al.join('\n'));
  assert.equal(ra.as.length, 4);
  assert.equal(ra.as[2].rows.find((y) => y.kind === 'cyc')!.since, 11);
  assert.equal(ra.as[2].free[0].above, '{image ntdll.dll+0x1000}');
  const at = report(al.join('\n'));
  assert.match(at, /native call sites \(allocsites, docs\/201, 1 in 64 heap blocks < 64 KB\): 4 samples; heap 100 -> 100 MB/);
  assert.match(at, /mark live-empty x3: growth to the next live-empty mark \(2 steps\); growth here survives removal:\n {4}SURVIVES \+3\.00 \+2\.50 MB \(sum 5\.5, live 44\.5\): heap 0x004E4931 < 0x00600000/);
  assert.equal(ra.as[2].untr[1].first, 12);
  assert.match(at, /untracked allocations >= 1 MB at #12: private 40\/306\.0MB, map 3\/34\.0MB, image 50\/400\.0MB/);
  assert.match(at, /\n {4}#12 mark live-empty: private 0x9EFC0000 16\.0 MB \(commit 16\.0, prot 0x4\) owner \?, new, inside the previous largest free block/);
  assert.match(at, /largest other listed at #12: private 0x10000000 8\.0 MB ts3native-allocsites since #10/);
  assert.match(at, /\n {4}\+0\.10 \+0\.00 MB .*virtual 0x00D7092A/);  // one step only: not SURVIVES
  assert.match(at, /held through live removal \(#10 -> #11\): heap 0x004E4931 .* 40\.0 -> 39\.0 MB/);
  assert.doesNotMatch(at, /held through .*d3d9\.dll/);  // the view dropped 32 -> 4
  assert.match(at, /free #1 0x9F000000 1211 MB: below \{k=virtual at=0x00D7092A from=0x00E4EF00\}, above \{image ntdll\.dll\+0x1000\}/);
  // --steady: 6 cycles; priv +5 per cycle after a +30 fill (leak), map steps 20/8/3/1/0 (settling), free flat after the fill
  assert.equal(steadyVerdict([30, 5, 5, 5, 5]), 'leak (~5.0 MB per cycle from cycle 2)');
  assert.equal(steadyVerdict([20, 8, 3, 1, 0]), 'settling (steps shrink to ~0)');
  assert.equal(steadyVerdict([12, 1, 0, -1, 0]), 'flat from cycle 2 (fill 12.0 in cycle 1->2)');
  assert.equal(steadyVerdict([0, 1]), 'too few cycles');
  const sl: string[] = ['ts3native 0.36.84 2026-10-26 09:00:00'];
  const mapS = [100, 120, 128, 131, 132, 132];
  let k = 100;
  const smp = (ev: string, priv: number, map: number, free: number) =>
    `diag: mem #${k++} mark ev=${ev} priv=${priv} free=${free} big=${free - 300}? map=${map} miCom=60 eaDir=160? t=${k}`;
  for (let c = 0; c < 6; c++) {
    const priv = 1500 + (c ? 30 + (c - 1) * 5 : 0), free = c ? 1570 : 1600;
    sl.push(smp('lss-full', priv + 400, mapS[c] + 50, free - 400), smp('gc-before', priv + 400, mapS[c] + 50, free - 400), smp('gc-after', priv + 380, mapS[c] + 50, free - 380));
    sl.push(smp('lss-empty', priv + 20, mapS[c], free - 20), smp('gc-before', priv + 20, mapS[c], free - 20), smp('gc-after', priv, mapS[c], free));
    if (c) sl.push(`allocsites: #${k} mark ev=lss-full cyc r=1 k=heap at=0x004E4931 from=0x00600000 MB=${40 + c * 2}.00 n=9 dMB=+${c === 1 ? 10 : 2}.00 dN=+1 aMB=1 fMB=1 since=#${k - 6}`,
      `allocsites: #${k} mark ev=lss-full cyc r=2 k=map at=d3d9.dll+0x1234 from=- MB=30.00 n=1 dMB=+${c === 1 ? 5 : 0}.00 dN=+0 aMB=1 fMB=1 since=#${k - 6}`);
    k++;
  }
  const st = steadyReport(sl.join('\n'), 'lss');
  assert.match(st, /6 x lss-full, 6 x lss-empty/);
  assert.match(st, /lss-empty: cycle[^]*\n {2}3 \| 1570 \(\+0\.0\) \| 1270 \(\+0\.0\) \| 1535 \(\+5\.0\) \| 128 \(\+8\.0\)/);  // gc-after values
  assert.match(st, /lss-empty:[^]*priv: leak \(~5\.0 MB per cycle from cycle 2\)/);
  assert.match(st, /lss-empty:[^]*map: settling/);
  assert.match(st, /lss-empty:[^]*free: flat from cycle 2 \(fill 30\.0 in cycle 1->2\)/);
  assert.match(st, /allocsites growing lss-full -> lss-full .*\(5 steps\): \n {2}\+10\.00 \+2\.00 \+2\.00 \+2\.00 \+2\.00 MB .*heap 0x004E4931/);
  assert.doesNotMatch(st, /d3d9\.dll/);  // grew in cycle 1->2 only
  assert.match(steadyReport(sl.join('\n'), 'nope'), /no nope-full/);
  console.log('memory-report self-check ok');
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  const { values, positionals } = parseArgs({ allowPositionals: true, options: { 'self-check': { type: 'boolean' }, json: { type: 'boolean' }, steady: { type: 'string' } } });
  if (values['self-check']) selfCheck();
  else if (!positionals[0]) { console.error('usage: node tools/memory-report.ts <ts3native.log> [--json | --steady <mark prefix>] | --self-check'); process.exit(2); }
  else {
    const text = readFileSync(positionals[0], 'utf8');
    console.log(values.steady ? steadyReport(text, values.steady) : values.json ? JSON.stringify(readLog(text), null, 1) : report(text));
  }
}
