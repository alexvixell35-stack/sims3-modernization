// Interaction sweep (docs/268): report over ts3agent interaction_sweep rows, merged with docs/267's never-seen pack types; and the
// generator of the repros.json sweep-* entries (one per pack, the pack's script types from docs/data/ep-pack-types.csv).
// Rows come from <tag>-repro.jsonl (the step answers, full rows) and from "isweep row {json}" lines in ts3native.log copies
// (cut to 470 characters, but they survive a crash); merged per pack + row index, the jsonl row wins.
//   node tools/harness/interaction-sweep-report.ts <profile tag | x-repro.jsonl | x-ts3native.log>... [--out file.md]
//   node tools/harness/interaction-sweep-report.ts --gen-repros       (re)write the contiguous sweep-* block at the end of repros.json
//   node tools/harness/interaction-sweep-report.ts --self-check
// Per pack: types, never seen (docs/267), rows, tested, ok (Test passed), pushed, ran (entered Run), failed, skipped (destructive),
// never available (a pack type with interaction definitions that no row came from), top fail reasons, top script errors; then
// per never-seen type with definitions: ran / pushed / test failed / never available.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(import.meta.dirname, '..', '..');
const CSV = join(ROOT, 'docs', 'data', 'ep-pack-types.csv');
const REPROS = join(ROOT, 'tools', 'harness', 'repros.json');
const PROFILE = join(ROOT, 'probe-out', 'profile');

export type Row = { i: number; pack: string; cls: string; tk: string; def: string; name?: string; test: string; actor?: string; fails?: string[];
  pushed?: boolean; ran?: boolean; imm?: boolean; ms?: number; se?: number; err?: string; ex?: string; why?: string; reset?: boolean; dialog?: boolean; picked?: string };
export type PackType = { pack: string; packName: string; type: string; defs: number; hot: number; seen: boolean };

// ts3agent DiagLine text reaches ts3native.log as "ts3fixes: <text>" (boat_getout_stress state lines, 2026-11-15 logs)
export const ROW_RE = /isweep row (\{.*\})\s*$/;
export const DONE_RE = /isweep done (\S+) state (\w+) rows (\d+) next (\d+)/;

export function loadTypes(path = CSV): PackType[] {
  const [head, ...lines] = readFileSync(path, 'utf8').trim().split(/\r?\n/);
  const h = head.split(','), col = (n: string) => h.indexOf(n);
  return lines.map(l => l.split(',')).map(c => ({ pack: c[col('pack')], packName: c[col('pack_name')], type: c[col('type')],
    defs: +c[col('interaction_defs')] || 0, hot: +c[col('hot_score')] || 0, seen: !!c[col('seen_runtime')] }));
}

// the pack type a definition belongs to: its outermost declaring class ("A.B+C+Definition:ActionKey" -> "A.B")
export const outerOf = (def: string) => def.split(':')[0].split('+')[0];

export function rowsFromJsonl(text: string): Row[] {
  const out: Row[] = [];
  for (const l of text.split(/\r?\n/)) {
    if (!l.trim()) continue;
    let o: any; try { o = JSON.parse(l); } catch { continue; }
    if (o.cmd !== 'interaction_sweep') continue;
    const res = o.r?.result ?? o.r;
    for (const row of res?.rows ?? []) out.push(row);
  }
  return out;
}

export function rowsFromLog(text: string): { rows: Row[]; done: { pack: string; state: string; rows: number; next: number }[] } {
  const rows: Row[] = [], done: { pack: string; state: string; rows: number; next: number }[] = [];
  for (const l of text.split(/\r?\n/)) {
    const m = ROW_RE.exec(l);
    if (m) { try { rows.push(JSON.parse(m[1])); } catch { /* a cut line */ } continue; }
    const d = DONE_RE.exec(l);
    if (d) done.push({ pack: d[1], state: d[2], rows: +d[3], next: +d[4] });
  }
  return { rows, done };
}

// jsonl rows (full) win over log rows (cut) with the same pack + index
export function mergeRows(full: Row[], cut: Row[]): Row[] {
  const by = new Map<string, Row>();
  for (const r of cut) by.set(`${r.pack}#${r.i}`, r);
  for (const r of full) by.set(`${r.pack}#${r.i}`, r);
  return [...by.values()].sort((a, b) => a.pack.localeCompare(b.pack) || a.i - b.i);
}

const top = (xs: string[], n = 5) => [...xs.reduce((m, x) => m.set(x, (m.get(x) ?? 0) + 1), new Map<string, number>())].sort((a, b) => b[1] - a[1]).slice(0, n);
// a fail reason without the actor and the Sim-specific tooltip text: "npc-adult: Tuning_DisallowUserDirected" -> the result code
const reasonKey = (f: string) => f.replace(/^[\w-]+: /, '').split(':')[0].trim();

export function summarize(rows: Row[], types: PackType[]) {
  const packs = [...new Set([...rows.map(r => r.pack)])].sort();
  return packs.map(pack => {
    const pr = rows.filter(r => r.pack === pack), pt = types.filter(t => t.pack === pack);
    const hit = new Map<string, Row[]>();
    for (const r of pr) for (const k of new Set([outerOf(r.def), r.cls])) hit.set(k, [...(hit.get(k) ?? []), r]);
    const status = (t: PackType) => { const rs = hit.get(t.type) ?? []; return !rs.length ? 'never available' : rs.some(r => r.ran) ? 'ran' : rs.some(r => r.pushed) ? 'pushed' : rs.some(r => r.test.startsWith('pass')) ? 'ok, not pushed' : rs.some(r => r.test.startsWith('skipped')) ? 'skipped' : 'test failed'; };
    const withDefs = pt.filter(t => t.defs > 0);
    return {
      pack, packName: pt[0]?.packName ?? '', types: pt.length, neverSeen: pt.filter(t => !t.seen).length,
      rows: pr.length, tested: pr.filter(r => !r.test.startsWith('skipped')).length, ok: pr.filter(r => r.test.startsWith('pass')).length,
      pushed: pr.filter(r => r.pushed).length, ran: pr.filter(r => r.ran).length, failed: pr.filter(r => r.test === 'fail').length,
      skipped: pr.filter(r => r.test.startsWith('skipped')).length, exceptions: pr.filter(r => r.ex).length, resets: pr.filter(r => r.reset).length,
      neverAvailable: withDefs.filter(t => status(t) === 'never available').map(t => t.type),
      failReasons: top(pr.flatMap(r => r.test === 'fail' ? (r.fails ?? []).map(reasonKey) : [])),
      scriptErrors: top(pr.filter(r => r.se).map(r => r.err ?? 'unknown')),
      seErrorRows: pr.filter(r => r.se).length,
      neverSeenStatus: withDefs.filter(t => !t.seen).map(t => ({ type: t.type, hot: t.hot, status: status(t) })),
    };
  });
}

export function markdown(sum: ReturnType<typeof summarize>, done: { pack: string; state: string; rows: number; next: number }[]): string {
  const o: string[] = ['# Interaction sweep report (docs/268)', '', '| Pack | Types | Never seen (docs/267) | Rows | Tested | OK | Pushed | Ran | Failed | Skipped | Never available | Script-error rows | Sweep end |', '|---|---|---|---|---|---|---|---|---|---|---|---|---|'];
  for (const s of sum) {
    const d = done.filter(x => x.pack === s.pack).at(-1);
    o.push(`| ${s.pack} ${s.packName} | ${s.types} | ${s.neverSeen} | ${s.rows} | ${s.tested} | ${s.ok} | ${s.pushed} | ${s.ran} | ${s.failed} | ${s.skipped} | ${s.neverAvailable.length} | ${s.seErrorRows} | ${d ? `${d.state}, next ${d.next}` : 'no done line'} |`);
  }
  for (const s of sum) {
    o.push('', `## ${s.pack} ${s.packName}`, '');
    o.push(`Top fail reasons: ${s.failReasons.map(([k, n]) => `${k} (${n})`).join(', ') || 'none'}.`);
    o.push(`Top script errors (findings, EA code unless a Ts3Fixes frame): ${s.scriptErrors.map(([k, n]) => `\`${k}\` (${n})`).join(', ') || 'none'}.`);
    const ns = s.neverSeenStatus;
    const by = (st: string) => ns.filter(x => x.status === st);
    o.push('', `Never-seen types with definitions (${ns.length}): ran ${by('ran').length}, pushed ${by('pushed').length}, ok not pushed ${by('ok, not pushed').length}, test failed ${by('test failed').length}, skipped ${by('skipped').length}, never available ${by('never available').length}.`);
    const hot = ns.filter(x => x.hot >= 2);
    if (hot.length) { o.push('', '| Likely hot, never seen | Status |', '|---|---|'); for (const x of hot) o.push(`| \`${x.type.replace(/^Sims3\.Gameplay\./, '')}\` | ${x.status} |`); }
  }
  return o.join('\n') + '\n';
}

// ------------------------------------------------------------ repros.json sweep-* entries
// home world per pack (docs/268 §6): the world whose lots fit the pack's content; destination worlds run via travel (queue)
export const HOME: Record<string, string> = { EP1: 'Baseline-SV', EP2: 'Baseline-BP', EP3: 'Baseline-BP', EP4: 'Baseline-SV', EP5: 'Baseline-IP', EP6: 'Baseline-BP',
  EP7: 'Baseline-SV', EP8: 'Baseline-SV', EP9: 'Baseline-SV', EP10: 'Baseline-IP', EP11: 'Baseline-SV', SP1: 'Baseline-BP', SP6: 'Baseline-BP', SP8: 'Baseline-BP', SP9: 'Baseline-BP' };
// preconditions forced before the sweep (docs/268 §4): season / moon for the packs whose definitions test them
const ENV: Record<string, object> = { EP7: { mode: 'fixed', season: 'fall', moon_phase: 0 }, EP8: { mode: 'fixed', season: 'winter' } };
// sweep time cap per pack (interaction_sweep maxMinutes): SPs bring a handful of types
export const sweepMin = (pack: string) => pack.startsWith('SP') ? 15 : 40;

export function sweepEntry(pack: string, types: PackType[]): any {
  const pt = types.filter(t => t.pack === pack);
  const env = ENV[pack], min = sweepMin(pack);
  return {
    id: `sweep-${pack.toLowerCase()}`,
    bug: `docs/267: ${pack} ${pt[0].packName} has ${pt.length} script types, ${pt.filter(t => !t.seen).length} never seen in our runs; their interactions never ran under our routing / Mono changes`,
    fix: 'measurement only: forced interaction sweep of every definition the pack brings (docs/268)',
    save: HOME[pack] ?? 'Baseline-BP',
    area: 'ep-coverage',
    note: `UNVERIFIED (static only, 2026-11-15): never run. Generated by node tools/harness/interaction-sweep-report.ts --gen-repros from docs/data/ep-pack-types.csv (do not edit by hand). interaction_sweep: pack objects (in-world, else bought and placed on the far community lot), pack definitions on every other in-world object type, terrain, lot, self and a social partner; NPC actors by age + pets (never the household); EA Test per actor, the first pass pushed (teleport + push in one slice), 4 s per run, cancel, reset when stuck. Destructive definitions skipped (docs/268 §5). Rows: <tag>-repro.jsonl and "isweep row" lines in ts3native.log; report: node tools/harness/interaction-sweep-report.ts <tag>.`,
    steps: [
      ...(env ? [{ cmd: 'test_env', args: env, waitS: 2, note: 'docs/268 §4 precondition (queue job noEnv: true keeps it)' }] : [{ cmd: 'test_env', args: { mode: 'read' }, waitS: 1, optional: true, note: 'log: season, weather, hour' }]),
      ...(pack === 'EP5' ? [{ cmd: 'spawn_horse', args: { count: 1, near: '@far' }, waitS: 5, optional: true, note: 'a horse actor for the horse definitions' }] : []),
      { cmd: 'interaction_sweep', args: { start: true, pack, types: pt.map(t => t.type), maxPerDef: 1, secsPerRun: 4, maxMinutes: min, near: '@far' }, waitS: 20, note: 'setup: actors, world scan, catalogue scan + placement' },
      { cmd: 'interaction_sweep', args: { result: true }, waitS: 1, note: 'rows so far; then wait for the end',
        until: { cmd: 'interaction_sweep', args: { result: true, peek: true }, all: [{ path: 'finished', equals: true }], everyS: 30, maxS: min * 60 + 300 } },
      { cmd: 'interaction_sweep', args: { result: true }, waitS: 1, note: 'rows' },
      { cmd: 'interaction_sweep', args: { result: true }, waitS: 1, note: 'rows (more than 1500)' },
      { cmd: 'interaction_sweep', args: { result: true, require: true, stop: true }, note: 'FAILS unless the sweep finished (state done) with >= 1 row' },
    ],
    expect: 'no crash; final step passes (state done, rows >= 1); "isweep done <pack> state done" in ts3native.log; script errors from EA interactions are findings (scriptErrorsReport), a Ts3Fixes / Ts3Agent frame FAILs',
  };
}

const PACK_ORDER = (p: string) => (p.startsWith('EP') ? 0 : 100) + +p.slice(2);
export const packsOf = (types: PackType[]) => [...new Set(types.map(t => t.pack))].sort((a, b) => PACK_ORDER(a) - PACK_ORDER(b));

// replace the contiguous sweep-* block at the end of repros.json (the file is CRLF, 2-space JSON; earlier entries stay byte for byte)
export function withSweepBlock(src: string, entries: any[]): string {
  const eol = src.includes('\r\n') ? '\r\n' : '\n';
  const start = /,\r?\n  \{\r?\n    "id": "sweep-/.exec(src);
  let head = src.replace(/\s*\]\s*$/, '');
  if (start) {
    const tail = JSON.parse('[' + src.slice(start.index + 1).replace(/\s*\]\s*$/, '') + ']');
    if (tail.some((e: any) => !String(e.id).startsWith('sweep-'))) throw new Error('repros.json: entries after the sweep-* block; keep the block last (contiguous)');
    head = src.slice(0, start.index);
  }
  const body = entries.map(e => JSON.stringify(e, null, 2).split('\n').map(l => '  ' + l).join(eol)).join(',' + eol);
  return head + ',' + eol + body + eol + ']' + eol;
}

// the scriptErrorsReport regex of the sweep queues: a ScriptError with a stack and no Ts3Fixes / Ts3Agent frame (EA code: a finding)
export const EA_ONLY_RE = '^(?![\\s\\S]*\\n#\\d+:[^\\n]* in (?:Ts3Fixes|Ts3Agent)\\.)[\\s\\S]*\\n#\\d+: 0x';

const argv = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url) ? process.argv.slice(2) : null;
if (argv) {
  const { values, positionals } = parseArgs({ args: argv, allowPositionals: true, options: { 'self-check': { type: 'boolean' }, 'gen-repros': { type: 'boolean' }, out: { type: 'string' } } });
  if (values['self-check']) await selfCheck();
  else if (values['gen-repros']) {
    const types = loadTypes(), src = readFileSync(REPROS, 'utf8');
    const out = withSweepBlock(src, packsOf(types).map(p => sweepEntry(p, types)));
    writeFileSync(REPROS, out);
    console.log(`repros.json: ${packsOf(types).length} sweep-* entries (${types.length} types), ${out.length - src.length >= 0 ? '+' : ''}${out.length - src.length} bytes`);
  } else {
    if (!positionals.length) throw new Error('usage: interaction-sweep-report.ts <profile tag | x-repro.jsonl | x-ts3native.log>... [--out file.md] | --gen-repros | --self-check');
    const full: Row[] = [], cut: Row[] = [], done: ReturnType<typeof rowsFromLog>['done'] = [];
    for (const p of positionals) {
      const files = p.endsWith('.jsonl') || p.endsWith('.log') ? [p] : [join(PROFILE, `${p}-repro.jsonl`)];
      for (const f of files) {
        if (!existsSync(f)) { console.error(`missing: ${f}`); continue; }
        const t = readFileSync(f, 'utf8');
        if (f.endsWith('.jsonl')) full.push(...rowsFromJsonl(t)); else { const r = rowsFromLog(t); cut.push(...r.rows); done.push(...r.done); }
      }
    }
    const md = markdown(summarize(mergeRows(full, cut), loadTypes()), done);
    if (values.out) { writeFileSync(values.out, md); console.log(`wrote ${values.out}`); } else process.stdout.write(md);
  }
}

async function selfCheck() {
  const assert = (await import('node:assert')).strict;
  // regexes: a line as the agent writes it (DiagLine text behind the "ts3fixes: " prefix) and real non-matching log lines
  const rowLine = 'ts3fixes: isweep row {"i":12,"pack":"EP1","cls":"Sims3.Gameplay.Objects.TombObjects.Sarcophagus","tk":"object","def":"Sims3.Gameplay.Objects.TombObjects.Sarcophagus+OpenSarcophagus+Definition","name":"Open","test":"pass","actor":"npc-adult","pushed":true,"ran":true,"ms":3980}';
  const doneLine = 'ts3fixes: isweep done EP1 state done rows 412 next 1890 pushed 140 ran 121 se 3';
  const otherLines = ['ts3fixes: boat_getout_stress: state Palmira Medina npc: windows 0, forced 0, posture StandingPosture, current none, queue 0, boat 32 m',
    'ts3fixes: isweep rows pending', 'ts3fixes: isweep done EP1 state'];
  assert.ok(ROW_RE.test(rowLine)); assert.ok(DONE_RE.test(doneLine));
  for (const l of otherLines) { assert.ok(!ROW_RE.test(l), l); assert.ok(!DONE_RE.test(l), l); }
  const lg = rowsFromLog([rowLine, doneLine, ...otherLines, 'ts3fixes: isweep row {"i":13,"pack":"EP1","cls":"x","tk":"obj'].join('\r\n'));
  assert.equal(lg.rows.length, 1, 'a cut row line is dropped'); assert.equal(lg.done[0].next, 1890);
  // scriptErrorsReport regex: real EA stack (probe-out ScriptError 2026-11-08 DanceFloor) matches; the same with a Ts3Fixes frame does not;
  // "Loaded Assemblies: Ts3Fixes" alone never makes an error ours
  const ea = '<NRaas.ErrorTrap>\n<Content>\nSystem.InvalidOperationException: Collection was modified;enumeration operation may not execute.\n#0: 0x0000e throw      in System.Collections.Generic.List`1+Enumerator:MoveNext () ()\n#1: 0x00037 call       in Sims3.Gameplay.Objects.Decorations.Sims3.Gameplay.Objects.Decorations.DanceFloor:FindAndRouteOntoADanceFloorWithFollower (Sims3.Gameplay.Actors.Sim,Sims3.Gameplay.Actors.Sim,Sims3.Gameplay.Interfaces.IDanceable) ([00000000] [00000000] [00000000] )\n#2: 0x00074 stfld.o    in Sims3.Gameplay.Objects.Electronics.Stereo+DanceTogetherA:BaseRun () ()\n\nLoaded Assemblies:\n Ts3Fixes\n</Content>';
  const ours = ea.replace('#2: 0x00074', '#2: 0x00010 call       in Ts3Fixes.Ts3Fixes.DanceFloorGuard:Run () ()\n#3: 0x00074');
  const agent = ea.replace('#2: 0x00074', '#2: 0x00010 call       in Ts3Agent.Ts3Agent.Handlers:IswRow () ()\n#3: 0x00074');
  const re = new RegExp(EA_ONLY_RE);
  assert.ok(re.test(ea), 'EA stack is a finding'); assert.ok(!re.test(ours), 'Ts3Fixes frame is ours'); assert.ok(!re.test(agent), 'Ts3Agent frame is ours');
  assert.ok(!re.test('<NRaas.ErrorTrap>\n-- Correction Logs --\nDestroyed Object Found: x\n'), 'no stack, no match');
  // the queues use exactly this regex, and their logRequire regex is DONE-shaped (queue.ts regexCases test it on real lines)
  for (const q of ['sweep-packs-home-1115.json', 'sweep-packs-dest-1115.json']) {
    const f = join(ROOT, 'tools', 'harness', 'queues', q);
    if (!existsSync(f)) continue;
    for (const j of JSON.parse(readFileSync(f, 'utf8')).jobs) {
      assert.deepEqual(j.scriptErrorsReport, [EA_ONLY_RE], `${q} ${j.id}: scriptErrorsReport`);
      assert.ok(Object.keys(j.logRequire ?? {}).some(k => new RegExp(k).test(doneLine.replace('EP1', j.run.profile.repro.slice(6).toUpperCase()))), `${q} ${j.id}: logRequire matches its done line`);
      assert.ok(j.run.profile.minutes * 60 >= sweepMin(j.run.profile.repro.slice(6).toUpperCase()) * 60 + 300, `${q} ${j.id}: profile window covers the sweep`);
    }
  }
  // outer type, merge, summary
  assert.equal(outerOf('Sims3.Gameplay.Socializing.SocialInteractionA+Definition:Flirt'), 'Sims3.Gameplay.Socializing.SocialInteractionA');
  const types: PackType[] = [{ pack: 'EP1', packName: 'World Adventures', type: 'A.Tomb', defs: 3, hot: 3, seen: false }, { pack: 'EP1', packName: 'World Adventures', type: 'A.Relic', defs: 2, hot: 2, seen: false },
    { pack: 'EP1', packName: 'World Adventures', type: 'A.Seen', defs: 1, hot: 0, seen: true }, { pack: 'EP1', packName: 'World Adventures', type: 'A.NoDefs', defs: 0, hot: 0, seen: false }];
  const rows: Row[] = [{ i: 0, pack: 'EP1', cls: 'A.Tomb', tk: 'object', def: 'A.Tomb+Open+Definition', test: 'pass', pushed: true, ran: true, se: 1, err: 'NullReferenceException @ at A.Tomb+Open:Run' },
    { i: 1, pack: 'EP1', cls: 'A.Tomb', tk: 'object', def: 'A.Tomb+Die+Definition', test: 'skipped: destructive (Die)' },
    { i: 2, pack: 'EP1', cls: 'Terrain', tk: 'terrain', def: 'A.Seen+Dig+Definition', test: 'fail', fails: ['npc-adult: Tuning_DisallowUserDirected', 'npc-teen: Fail: too young'] }];
  const merged = mergeRows([rows[0]], [{ ...rows[0], ran: false }, rows[1], rows[2]]);
  assert.equal(merged.length, 3); assert.equal(merged[0].ran, true, 'jsonl row wins');
  const [s] = summarize(merged, types);
  assert.deepEqual([s.rows, s.tested, s.ok, s.pushed, s.ran, s.failed, s.skipped, s.seErrorRows], [3, 2, 1, 1, 1, 1, 1, 1]);
  assert.deepEqual(s.neverAvailable, ['A.Relic']);
  assert.deepEqual(s.neverSeenStatus.map(x => x.status), ['ran', 'never available']);
  assert.deepEqual(s.failReasons.map(x => x[0]), ['Tuning_DisallowUserDirected', 'Fail']);
  assert.ok(markdown([s], [{ pack: 'EP1', state: 'done', rows: 3, next: 9 }]).includes('| EP1 World Adventures | 4 | 3 | 3 | 2 | 1 | 1 | 1 | 1 | 1 | 1 | 1 | done, next 9 |'));
  // repros block: generated entries match the CSV, block contiguous and last; regenerating is a no-op
  const all = loadTypes();
  assert.ok(all.length > 1000 && packsOf(all).includes('EP11'));
  const src = readFileSync(REPROS, 'utf8'), want = withSweepBlock(src, packsOf(all).map(p => sweepEntry(p, all)));
  assert.equal(src, want, 'repros.json sweep-* block is stale: run node tools/harness/interaction-sweep-report.ts --gen-repros');
  const toy = '[\r\n  {\r\n    "id": "a"\r\n  }\r\n]\r\n', once = withSweepBlock(toy, [{ id: 'sweep-x' }]);
  assert.equal(withSweepBlock(once, [{ id: 'sweep-x' }]), once, 'idempotent');
  assert.deepEqual(JSON.parse(once).map((e: any) => e.id), ['a', 'sweep-x']);
  assert.throws(() => withSweepBlock(once.replace(/\r\n\]\r\n$/, ',\r\n  {\r\n    "id": "b"\r\n  }\r\n]\r\n'), []), /contiguous/);
  console.log('interaction-sweep-report self-check ok');
}
