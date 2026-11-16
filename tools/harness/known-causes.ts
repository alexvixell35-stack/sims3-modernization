// Known third-party crash and hang causes (known-causes.json, Rodney 2026-11-16): queue.ts marks a job that ends in one of them
// VOID ("known cause <id> (<owner>)") and relaunches it once; a second hit is a normal CRASH/FAIL row. Ours until proven: only
// exact signatures match (crash: exception type + fault module + fault RVA; hang: a dump lock cycle wholly inside the listed module).
//   node tools/harness/known-causes.ts <text file>...   print the matching cause (or 'none') per file (a crash-ledger triage block or a job log)
//   node tools/harness/known-causes.ts --self-check     every entry's real-line cases + the queue helpers
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

export type KnownCause = { id: string; kind: 'crash' | 'hang'; owner: string; upstream: string; doc: string;
  exception?: string; module?: string; rva?: string; cycleModules?: string;
  cases?: { match?: string[]; weakMatch?: string[]; noMatch?: string[] } };
export type CauseHit = { id: string; owner: string; upstream: string; doc: string; weak: string; evidence: string };
const LIST = join(import.meta.dirname, 'known-causes.json');
export const loadCauses = (path = LIST): KnownCause[] => JSON.parse(readFileSync(path, 'utf8')).causes;

// crash-ledger.ts triage line: "  <ISO time>Z  <type>  at 0x<addr> in <module>[+0x<rva>]  stack: ..."
const CRASH_LINE = /^\s*\S+Z {2}(.+?) {2}at (0x[0-9a-f]+) in ([^\s+]+)(?:\+(0x[0-9a-f]+))? {2}stack:/gim;
// dump-locks.ts summary (game-alive.ts appends it to the kill dump line): "...; deadlock cycle a>b>a[; ...]; cycle modules: X[, Y]"
const CYCLE_MODS = /lock waits: \d+ of \d+ threads; deadlock cycle [\d>]+(?:; [^;\r\n]*)*?; cycle modules: ([^;\r\n]+)/g;

function crashHit(c: KnownCause, text: string): { weak: string; evidence: string } | null {
  const want = parseInt(c.rva!, 16);
  for (const m of text.matchAll(CRASH_LINE)) {
    if (!m[1].startsWith(c.exception!) || m[3].toLowerCase() !== c.module!.toLowerCase()) continue;
    if (m[4]) { if (parseInt(m[4], 16) === want) return { weak: '', evidence: m[0].trim() }; continue; }
    // shortcut: no module base in the report, low 16 bits only (module bases are 64 KB aligned); a report with its module list gives the exact RVA
    if ((parseInt(m[2], 16) & 0xffff) === (want & 0xffff)) return { weak: 'weak match: low 16 bits of the address only, the report had no module base', evidence: m[0].trim() };
  }
  return null;
}
function hangHit(c: KnownCause, text: string): { weak: string; evidence: string } | null {
  const all = [...text.matchAll(CYCLE_MODS)];
  // every dump of the job must name only this module: one cycle elsewhere and the hang is not this cause
  return all.length && all.every(m => m[1].trim() === c.cycleModules) ? { weak: '', evidence: all.at(-1)![0] } : null;
}
// crash: the triage block of a crashed job (row.crash); hang: the job's log, only for a job that did not crash
export function matchCause(o: { crashed: boolean; crash?: string; jobLog?: string }, causes: KnownCause[] = loadCauses()): CauseHit | null {
  for (const c of causes) {
    const h = c.kind === 'crash' ? (o.crashed ? crashHit(c, o.crash ?? '') : null) : (!o.crashed ? hangHit(c, o.jobLog ?? '') : null);
    if (h) return { id: c.id, owner: c.owner, upstream: c.upstream, doc: c.doc, ...h };
  }
  return null;
}

export const KC_SUFFIX = '-kc';
export const voidLine = (k: CauseHit, id: string) =>
  `VOID: known cause ${k.id} (${k.owner})${k.weak ? `, ${k.weak}` : ''}: run void, relaunched once as ${id}; ${k.upstream} (${k.doc})`;
// the first hit's row: VOID (run-verdict.ts reads "run void"), its old FAIL lines kept as "first attempt (known cause): ..." (idempotent)
export function voidRow(row: { verdict: string[]; error?: string | null; selfCheckFail?: string; knownCause?: CauseHit; relaunchedAs?: string; kcWas?: string[] }, k: CauseHit, id: string) {
  const old = row.verdict.filter(v => !/^(VOID: known cause |first attempt \(known cause\): )/.test(v));
  if (old.length || row.error || row.selfCheckFail) row.kcWas = [...(row.kcWas ?? []), ...old, ...(row.error ? [`error: ${row.error}`] : []), ...(row.selfCheckFail ? [`self-check: ${row.selfCheckFail}`] : [])];
  row.verdict = [voidLine(k, id), ...(row.kcWas ?? []).map(v => `first attempt (known cause): ${v}`)];
  row.error = null; delete row.selfCheckFail; row.knownCause = k; row.relaunchedAs = id;
}

if (import.meta.main) {
  const args = process.argv.slice(2);
  if (args[0] === '--self-check') {
    const assert = (await import('node:assert')).strict;
    const causes = loadCauses();
    assert.ok(causes.length >= 2 && new Set(causes.map(c => c.id)).size === causes.length, 'unique ids');
    for (const c of causes) {
      assert.ok(c.owner && c.upstream && c.doc, `${c.id}: owner, upstream, doc`);
      if (c.kind === 'crash') assert.ok(c.exception && c.module && /^0x[0-9a-f]+$/i.test(c.rva ?? ''), `${c.id}: crash rule needs exception, module, rva`);
      else assert.ok(c.kind === 'hang' && c.cycleModules, `${c.id}: hang rule needs cycleModules`);
      const run = (t: string) => matchCause(c.kind === 'crash' ? { crashed: true, crash: t } : { crashed: false, jobLog: t }, [c]);
      assert.ok(c.cases?.match?.length && c.cases.noMatch?.length, `${c.id}: real match and noMatch lines`);
      for (const t of c.cases.match!) { const h = run(t); assert.ok(h && !h.weak, `${c.id} must match exactly: ${t}`); }
      for (const t of c.cases.weakMatch ?? []) assert.match(run(t)?.weak ?? '', /^weak match/, `${c.id} weak: ${t}`);
      for (const t of c.cases.noMatch!) assert.equal(run(t), null, `${c.id} must not match: ${t}`);
    }
    // kind gates: a crash block never matches a hang rule and a crashed job's log never matches a hang rule
    const hang = causes.find(c => c.kind === 'hang')!, crash = causes.find(c => c.kind === 'crash')!;
    assert.equal(matchCause({ crashed: true, crash: crash.cases!.match![0], jobLog: hang.cases!.match![0] }, causes)?.id, crash.id);
    assert.equal(matchCause({ crashed: true, crash: 'xcpt x\n  no report', jobLog: hang.cases!.match![0] }, causes), null, 'crashed: hang rules off');
    assert.equal(matchCause({ crashed: false, crash: crash.cases!.match![0] }, causes), null, 'not crashed: crash rules off');
    assert.equal(matchCause({ crashed: false, jobLog: `${hang.cases!.match![0]}\n${hang.cases!.noMatch![0]}` }, causes), null, 'two dumps, one cycle elsewhere: no match');
    // the row: VOID, old FAIL lines kept and no longer FAIL lines, idempotent
    const k = matchCause({ crashed: true, crash: crash.cases!.match![0] }, causes)!;
    const row: any = { verdict: ['FAIL: events not fired: log x 0 lines'], error: 'cycle.ts exit 1', selfCheckFail: 's', crashed: true };
    voidRow(row, k, `sv-x${KC_SUFFIX}`); voidRow(row, k, `sv-x${KC_SUFFIX}`);
    assert.equal(row.verdict[0], 'VOID: known cause apex-0010-backbuffer (Apex Radiance 4.0.1 bridge client, patch 0010): run void, relaunched once as sv-x-kc; fixed by our bridge patch 0016, https://github.com/loinyx/Sims3-ApexRadiance/pull/89 (docs/270 §29)');
    assert.deepEqual(row.verdict.slice(1), ['first attempt (known cause): FAIL: events not fired: log x 0 lines', 'first attempt (known cause): error: cycle.ts exit 1', 'first attempt (known cause): self-check: s']);
    assert.ok(row.error === null && row.selfCheckFail === undefined && row.relaunchedAs === 'sv-x-kc' && row.knownCause.id === k.id);
    assert.ok(!row.verdict.some((v: string) => /^(FAIL|UNKNOWN): |^[\w.\- ]+: (FAIL|UNKNOWN)\b/.test(v)), 'no FAIL line left (queue.ts isFail)');
    assert.match(row.verdict[0], /run void/, 'run-verdict.ts VOID_RE');
    console.log('known-causes self-check: ok');
  } else if (args.length) {
    for (const f of args) { const t = readFileSync(f, 'utf8'), h = matchCause({ crashed: true, crash: t }) ?? matchCause({ crashed: false, jobLog: t }); console.log(`${f}: ${h ? `${h.id} (${h.owner})${h.weak ? `, ${h.weak}` : ''}` : 'none'}`); }
  } else { console.error('usage: node tools/harness/known-causes.ts <file>... | --self-check'); process.exit(2); }
}
