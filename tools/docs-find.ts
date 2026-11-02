// Finds the docs for a topic, an address or a switch, and writes docs/index-by-area.md (one line per doc, grouped by area),
// so sessions can search instead of loading every doc's index line. docs/index.md (hand-written, one line per doc or doc
// group, moved there from CLAUDE.md) stays the source of the summaries; this script never writes it.
//   node tools/docs-find.ts <words...>                   top 5 docs/*.md by title, file name, index line, headings
//   node tools/docs-find.ts --addr 0x82EDB0              docs that mention an address (0x82EDB0, 0x0082EDB0, 82EDB0)
//   node tools/docs-find.ts --switch "[RouteAsync] JobYield"   docs for a switch, section, ts3fixes switch or counter
//   node tools/docs-find.ts --write-index                (re)write docs/index-by-area.md; --check: exit 1 when it is stale
//   [--all] every hit, [--json]; node tools/docs-find.ts --self-check
// Summary per doc: its docs/index.md line (or a CLAUDE.md "Docs" line) when it has one, else its line in the current
// docs/index-by-area.md, else its title. Short refs (`docs/67`, `` `67` `` in a list line) map to every doc with
// that number (110, 113 and 125 each have two).
import { existsSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';

export const GEN = 'index-by-area.md';
// Index lines: docs/index.md first (read as a "## Docs" section), then CLAUDE.md's "## Docs" section; first line per doc wins
export function indexSource(root: string): string[] {
  const rd = (f: string) => existsSync(f) ? readFileSync(f, 'utf8') : '';
  return ['## Docs\n' + rd(join(root, 'docs', 'index.md')), rd(join(root, 'CLAUDE.md'))];
}
export interface Doc { file: string; num: string; title: string; heads: string[]; index: string; text: string }

const clip = (s: string, n = 160) => s.length <= n ? s : s.slice(0, s.lastIndexOf(' ', n - 1) > n / 2 ? s.lastIndexOf(' ', n - 1) : n - 1) + '...';
// routing, routes, routed -> rout; freezes, freeze -> freez
export const stem = (w: string) => w.toLowerCase().replace(/(ing|ed|es|s|e)$/, '');
const words = (s: string) => (s.toLowerCase().match(/[a-z0-9+_.*]+/g) ?? []).map(w => stem(w.replace(/^[._]+|[._]+$/g, ''))).filter(Boolean);

// CLAUDE.md "Docs" section: `- `docs/NNN-name.md` (tag): text` (one doc), or list items `docs/32` label, `33` label,
// `docs/05-x.md` (label) ... where an item starts after the bullet, a colon, a comma or a semicolon
export function indexLines(claude: string, files: string[]): Map<string, string> {
  const out = new Map<string, string>(), byNum = (n: string) => files.filter(f => f.startsWith(`${n}-`) || f.startsWith(`${n.padStart(2, '0')}-`));
  const sec = claude.split(/^## /m).find(s => s.startsWith('Docs')) ?? '';
  for (const l of sec.split(/\r?\n/)) {
    const m = l.match(/^- `docs\/([^`]+\.md)`(?: \(([^)]*)\))?: (.*)$/);
    if (m && files.includes(m[1])) { out.set(m[1], (m[2] ? `(${m[2]}) ` : '') + m[3]); continue; }
    const items = [...l.matchAll(/(?:^-[^`]*?|[:,;]\s*)`(?:docs\/([\w.-]+\.md)|(?:docs\/)?(\d{2,3}[a-z]?))`/g)];
    items.forEach((x, i) => {
      const end = i + 1 < items.length ? items[i + 1].index : l.length;
      const label = l.slice(x.index + x[0].length, end).trim().replace(/^\((.*)\)$/, '$1').replace(/^[+:]\s*/, '');
      for (const f of x[1] ? (files.includes(x[1]) ? [x[1]] : []) : byNum(x[2])) if (!out.has(f) && label) out.set(f, label);
    });
  }
  return out;
}

export function loadDocs(root: string): Doc[] {
  const dir = join(root, 'docs'), files = readdirSync(dir).filter(f => f.endsWith('.md') && f !== 'index.md' && f !== GEN).sort();
  const idx = new Map(indexSource(root).flatMap(t => [...indexLines(t, files)]).reverse());
  const prev = new Map([...(existsSync(join(dir, GEN)) ? readFileSync(join(dir, GEN), 'utf8') : '').matchAll(/^- \[[^\]]+\]\(([^)]+)\): (.*)$/gm)].map(m => [m[1], m[2]]));
  return files.map(f => {
    const text = readFileSync(join(dir, f), 'utf8'), heads = [...text.matchAll(/^#{1,4} (.+)$/gm)].map(m => m[1]);
    const title = (heads[0] ?? f).replace(/^\d+[a-z]?\.\s*/, ''), old = prev.get(f);
    return { file: f, num: f.match(/^(\d+)/)?.[1] ?? '', title, heads: heads.slice(1), index: idx.get(f) ?? (old !== title && old ? old : ''), text };
  });
}

export const summary = (d: Doc) => clip(d.index || d.title);

// title + file name 5, index line 4, headings 2 (per word, once each), body 1 with a log cap; all words present: x2
export function rank(docs: Doc[], query: string[]): { d: Doc; s: number }[] {
  const q = query.flatMap(words);
  if (!q.length) return [];
  return docs.map(d => {
    const T = new Set(words(d.title + ' ' + d.file.replace(/\.md$/, '').replace(/-/g, ' '))), I = new Set(words(d.index)), H = new Set(words(d.heads.join(' ')));
    const body = words(d.text);
    let s = 0, all = true;
    for (const w of q) {
      const n = body.filter(x => x === w).length, sw = (T.has(w) ? 5 : 0) + (I.has(w) ? 4 : 0) + (H.has(w) ? 2 : 0) + Math.min(3, Math.log2(1 + n));
      if (!sw) all = false;
      s += sw;
    }
    return { d, s: all && q.length > 1 ? s * 2 : s };
  }).filter(x => x.s > 0).sort((a, b) => b.s - a.s || a.d.file.localeCompare(b.d.file));
}

// the doc's own title or index line names it: that doc is about it, not one of many mentions
const boost = (d: Doc, re: RegExp) => new RegExp(re.source, re.flags.replace('g', '')).test(d.title + ' ' + d.index) ? 20 : 0;

export function findAddr(docs: Doc[], addr: string): { d: Doc; s: number }[] {
  const h = addr.replace(/^0x/i, '').replace(/^0+/, '').toUpperCase();
  if (!/^[0-9A-F]{1,8}$/.test(h)) throw new Error(`not a hex address: ${addr}`);
  const re = new RegExp(`(?<![0-9A-Za-z_])(?:0x)?0*${h}(?![0-9A-Fa-f])`, 'gi');
  return docs.map(d => { const n = d.text.match(re)?.length ?? 0; return { d, s: n && n + boost(d, re) }; }).filter(x => x.s).sort((a, b) => b.s - a.s || a.d.file.localeCompare(b.d.file));
}

// "[Section] Key": the pair written together counts 10, Key in a doc that names [Section] 3, [Section] alone 1;
// "[Section]" or a bare word (ts3fixes switch `jobyield`, counter prefix `jy*`): whole-word hits, any case
export function findSwitch(docs: Doc[], sw: string): { d: Doc; s: number }[] {
  const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), m = sw.trim().match(/^(\[[^\]]+\])\s*(\S+)?$/);
  const count = (t: string, re: RegExp) => t.match(re)?.length ?? 0;
  return docs.map(d => {
    if (m && m[2]) {
      const pair = count(d.text, new RegExp(`${esc(m[1])}\\s*\`?\\s*${esc(m[2])}(?![\\w])`, 'g')), sec = count(d.text, new RegExp(esc(m[1]), 'g'));
      const key = sec ? count(d.text, new RegExp(`(?<![\\w])${esc(m[2])}(?![\\w])`, 'g')) : 0;
      const s = pair * 10 + (key - pair) * 3 + Math.min(sec, 5);
      return { d, s: s && s + boost(d, new RegExp(`(?<![\\w])${esc(m[2])}(?![\\w])`)) };
    }
    const w = sw.trim().replace(/\*$/, '');
    const re = new RegExp(m ? esc(w) : `(?<![\\w])${esc(w)}${sw.trim().endsWith('*') ? '\\w*' : ''}(?![\\w])`, m ? 'g' : 'gi'), n = count(d.text, re);
    return { d, s: n && n + boost(d, re) };
  }).filter(x => x.s > 0).sort((a, b) => b.s - a.s || a.d.file.localeCompare(b.d.file));
}

// --write-index: areas by keyword hits in title + file name (x3) and index line; engine maps = the CLAUDE.md "Engine maps" line
export const AREAS: [string, RegExp][] = [
  ['Routing', /rout|steer|path|planner|\bp[3-6]\b|clone|sync plan|wait reducer|stalker|footprint|portal|door|locomotion|quadtree|lot.?table|placement|avoidance|replan|0x918|gate[ -]?\d|unseen|stuck/g],
  ['Mono and scripts', /mono|mint|wildflower|spawner|threadedobjects|taskobjects|autonom|autshadow|go-?live|interpreter|managed|script|jit|offload|cctor|nraas|errortrap|overwatch|alarm|broadcast/g],
  ['Memory and saves', /memory|address.space|leak|heap|alloc|largest free|\bgc\b|garbage|compaction|save|texture|vram|cache/g],
  ['Crashes', /crash|exception|error 12|triage|xcpt|\bhangs?\b/g],
  ['Hitches and the main thread', /hitch|stutter|frame|main.thread|stall|animation|affinity|spin|lock wait/g],
  ['Travel and gameplay', /travel|vacation|household|store content|online|gameplay/g],
  ['Player tools and releases', /player|doctor|diagnos|install|release|compat|porting|guide|faq|requirement|nexus|edition/g],
];
export function areaOf(d: Doc, engine: Set<string>): string {
  if (engine.has(d.num)) return 'Engine maps';
  const t = (d.title + ' ' + d.file).toLowerCase(), i = d.index.toLowerCase();
  let best = 'Other (process, measurements, ideas)', bs = 0;
  for (const [a, re] of AREAS) { const s = 3 * (t.match(re)?.length ?? 0) + (i.match(re)?.length ?? 0); if (s > bs) bs = s, best = a; }
  return best;
}
export function indexText(docs: Doc[], claude: string): string {
  const eng = new Set([...(claude.match(/^- Engine maps.*$/m)?.[0] ?? '').matchAll(/`(?:docs\/)?(\d{2,3})`/g)].map(m => m[1]));
  const groups = new Map<string, Doc[]>();
  for (const d of docs) { const a = areaOf(d, eng); groups.set(a, [...(groups.get(a) ?? []), d]); }
  const order = [...AREAS.map(a => a[0]), 'Engine maps', 'Other (process, measurements, ideas)'];
  const o = ['# Docs by area', '','Generated by `node tools/docs-find.ts --write-index`: each doc\'s [docs/index.md](index.md) line, else its line here, else its title.',
    'Edit a summary in docs/index.md (the source), then rerun; areas come from keywords. Search instead of reading it all: `node tools/docs-find.ts <words>`, `--addr 0x82EDB0`, `--switch "[RouteAsync] JobYield"`.', ''];
  for (const a of order) {
    const g = groups.get(a);
    if (!g) continue;
    o.push(`## ${a} (${g.length})`, '', ...g.map(d => `- [${d.file.replace(/\.md$/, '')}](${d.file}): ${d.index || d.title}`), '');
  }
  return o.join('\n');
}

function show(hits: { d: Doc; s: number }[], all: boolean, json: boolean, what: string): string[] {
  const top = all ? hits : hits.slice(0, 5);
  if (json) return [JSON.stringify(top.map(h => ({ file: `docs/${h.d.file}`, score: +h.s.toFixed(2), summary: summary(h.d) })), null, 2)];
  return [...top.map(h => `docs/${h.d.file}: ${summary(h.d)}`), ...(!all && hits.length > 5 ? [`(${hits.length - 5} more: --all)`] : []),
    `needs review: ${hits.length ? 'none' : `no doc matches ${what}`}`];
}

function selfCheck() {
  let k = 0;
  const t = (c: unknown, m: string) => { console.assert(c, m); if (!c) process.exitCode = 1; k++; };
  const root = mkdtempSync(join(tmpdir(), 'docs-find-')), dd = join(root, 'docs');
  mkdirSync(dd);
  const put = (f: string, s: string) => writeFileSync(join(dd, f), s);
  put('81-steering-lock.md', '# 81. Steering lock\n\n## Try-lock design\nThe per-tick acquire at `0x82EDB0` and 0x0082EDB0 again; not 0x82EDB01.\n');
  put('183-job-yield.md', '# 183. Job yield\n\n`[RouteAsync] JobYield` and ts3fixes `jobyield`; counters `jy*`: jyWait, jyHits.\n[RouteAsync] JobYield=1 in the queue.\n');
  put('134-route-sync-fallback-freezes.md', '# 134. Route sync fallback freezes\n\n[RouteAsync] FallbackAsync=1. A routing freeze; JobYield is elsewhere.\n');
  put('110-wildflower-a.md', '# 110. Wildflower stall\n\nGarbage text about memory.\n');
  put('110-wildflower-b.md', '# 110. Stub\n\nMoved.\n');
  put('32-middleware.md', '# 32. Middleware inventory\n\nLibraries.\n');
  put('67-crash-triage.md', '# 67. Crash triage: three crashes\n\nException at TS3W+0x1DDFBD.\n');
  const claude = ['# x', '## Docs', '- `docs/183-job-yield.md`: `[RouteAsync] JobYield` + ts3fixes `jobyield` (v0.1.6)',
    '- Engine maps (static): `docs/32` middleware inventory, `33` physics', '- Investigations: `docs/110` wildflower stall, `81` steering lock (`0x82EDB0`); `67` crash triage',
    '- `docs/183-job-yield.md` (not this one: first line wins), `docs/67-crash-triage.md` (triage of three crashes)', '## Working style', '- `docs/81-steering-lock.md`: not in the Docs section'].join('\n');
  writeFileSync(join(root, 'CLAUDE.md'), claude);
  const docs = loadDocs(root), by = (f: string) => docs.find(d => d.file === f)!;
  t(stem('routing') === stem('route') && stem('freezes') === stem('freeze'), 'stem');
  t(by('183-job-yield.md').index.startsWith('`[RouteAsync] JobYield`') && by('32-middleware.md').index === 'middleware inventory', 'index lines: exact and list');
  t(by('110-wildflower-a.md').index === 'wildflower stall' && by('110-wildflower-b.md').index === 'wildflower stall', 'short ref maps to both docs of a number');
  t(by('81-steering-lock.md').index === 'steering lock (`0x82EDB0`)', 'label with backticks; only the Docs section counts');
  t(by('67-crash-triage.md').index === 'crash triage' && indexLines('## Docs\n- `docs/05-a.md` (measurements), `docs/06` (harness)', ['05-a.md', '06-b.md']).get('06-b.md') === 'harness', 'list of full names and numbers');
  t(by('67-crash-triage.md').title === 'Crash triage: three crashes' && summary(by('134-route-sync-fallback-freezes.md')) === 'Route sync fallback freezes', 'title + summary fallback');
  const r = rank(docs, ['routing', 'freezes']);
  t(r[0]?.d.file === '134-route-sync-fallback-freezes.md', 'rank: title words, stems, all-words bonus');
  t(rank(docs, ['steering'])[0]?.d.file === '81-steering-lock.md' && rank(docs, ['zzz']).length === 0, 'rank: single word, no match');
  const a = findAddr(docs, '0x82EDB0');
  t(a.length === 1 && a[0].s === 22 && findAddr(docs, "0082edb0")[0]?.s === 22, 'addr: 0x/zero-padded forms, not a longer number; index line names it: +20');
  const s = findSwitch(docs, '[RouteAsync] JobYield');
  t(s[0]?.d.file === '183-job-yield.md' && s[1]?.d.file === '134-route-sync-fallback-freezes.md' && s[0].s > s[1].s, 'switch: pair beats a section + key elsewhere');
  t(findSwitch(docs, 'jobyield')[0]?.d.file === '183-job-yield.md' && findSwitch(docs, 'jy*')[0]?.s === 3 && findSwitch(docs, '[RouteAsync]').length === 2, 'switch: bare word, counter prefix, section alone');
  const ix = indexText(docs, claude);
  t(/## Routing \(3\)\n\n- \[134-route-sync-fallback-freezes\]/.test(ix) && ix.includes('## Engine maps (1)\n\n- [32-middleware](32-middleware.md): middleware inventory'), 'index: areas, engine maps from CLAUDE.md');
  t(ix.includes('## Crashes (1)') && ix.includes('- [183-job-yield](183-job-yield.md): `[RouteAsync] JobYield`'), 'index: crash area, link + summary');
  t(show(r, false, false, 'x').at(-1) === 'needs review: none' && show([], false, false, '"zzz"').at(-1) === 'needs review: no doc matches "zzz"', 'needs review line');
  writeFileSync(join(dd, GEN), ix);  // CLAUDE.md slimmed: the summaries come from the generated index itself
  writeFileSync(join(root, 'CLAUDE.md'), '# x\n## Docs\n- Engine maps: `docs/32` middleware inventory\n');
  const slim = loadDocs(root);
  t(slim.find(d => d.file === '183-job-yield.md')!.index.startsWith('`[RouteAsync] JobYield`') && slim.find(d => d.file === '134-route-sync-fallback-freezes.md')!.index === ''
    && indexText(slim, indexSource(root).join('\n')) === ix, 'slim CLAUDE.md: summaries kept from the generated index, same index');
  writeFileSync(join(dd, 'index.md'), '# Docs index\n\nIntro.\n\n- `docs/134-route-sync-fallback-freezes.md` (routing): sync fallbacks\n');
  t(loadDocs(root).find(d => d.file === '134-route-sync-fallback-freezes.md')!.index === '(routing) sync fallbacks' && !loadDocs(root).some(d => d.file === GEN || d.file === 'index.md'), 'docs/index.md lines are the source; neither index is a doc');
  rmSync(root, { recursive: true, force: true });
  console.log(`docs-find self-check: ${k} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  const { values: v, positionals: p } = parseArgs({ allowPositionals: true, options: {
    addr: { type: 'string' }, switch: { type: 'string' }, 'write-index': { type: 'boolean' }, check: { type: 'boolean' },
    all: { type: 'boolean' }, json: { type: 'boolean' }, 'self-check': { type: 'boolean' } } });
  const root = resolve(import.meta.dirname, '..');
  if (v['self-check']) selfCheck();
  else if (v['write-index']) {
    const docs = loadDocs(root), text = indexText(docs, indexSource(root).join('\n')), f = join(root, 'docs', GEN);
    const old = existsSync(f) ? readFileSync(f, 'utf8').replace(/\r\n/g, '\n') : '', other = text.match(/^## Other.*\((\d+)\)$/m)?.[1] ?? 0;
    if (v.check) { console.log(old === text ? `docs/${GEN} is current` : `docs/${GEN} is stale: node tools/docs-find.ts --write-index`); process.exitCode = old === text ? 0 : 1; }
    else { writeFileSync(f, text); console.log(`docs/${GEN}: ${docs.length} docs, ${docs.filter(d => !d.index).length} without a summary (title used)`); }
    console.log(`needs review: ${other} docs in "Other" (no area keyword)`);
  } else {
    const docs = loadDocs(root);
    const hits = v.addr ? findAddr(docs, v.addr) : v.switch ? findSwitch(docs, v.switch) : rank(docs, p);
    if (!v.addr && !v.switch && !p.length) { console.error('usage: node tools/docs-find.ts <words...> | --addr <hex> | --switch "<[Section] Key>" | --write-index [--check] [--all] [--json]'); process.exit(2); }
    console.log(show(hits, !!v.all, !!v.json, v.addr ?? v.switch ?? `"${p.join(' ')}"`).join('\n'));
  }
}
