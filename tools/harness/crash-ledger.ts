// Ties crashes to our changes. Every test start records the active change set; triage maps each game crash
// report (xcpt*.txt) to the change set that was active then and to what changed just before it.
//   node tools/harness/crash-ledger.ts snapshot [--note "why"]   append the current change set (only if it changed)
//   node tools/harness/crash-ledger.ts triage [--since <iso>]    list every crash report (written at or after --since) with its change set
// cycle.ts --save calls snapshot, so every harness run is covered.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFileSync, existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { BIN, GAME_EXE } from './game-exe.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const LEDGER = join(ROOT, 'docs', 'data', 'change-ledger.jsonl');
const USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
const STATE = join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local'), 'ts3-fixes', 'state.json');

type Entry = { at: string; key: string; note?: string; git: string; fixes: string[]; files: Record<string, string>; mods: string[]; options: Record<string, string> };

const sha = (b: Buffer | string) => createHash('sha256').update(b).digest('hex').slice(0, 12);
const fileHash = (p: string) => (existsSync(p) ? sha(readFileSync(p)) : 'missing');

function current(note?: string): Entry {
  let git = 'unknown';
  try {
    git = execFileSync('git', ['-C', ROOT, 'rev-parse', '--short', 'HEAD']).toString().trim();
    // docs/ (incl. this ledger) changes do not alter what runs
    if (execFileSync('git', ['-C', ROOT, 'status', '--porcelain', '--', '.', ':!docs']).toString().trim()) git += '+dirty';
  } catch { /* not a repo */ }
  const state: Record<string, { version: string | null; files: { path: string }[] }> = existsSync(STATE) ? JSON.parse(readFileSync(STATE, 'utf8')) : {};
  const files: Record<string, string> = { [GAME_EXE]: fileHash(join(BIN, GAME_EXE)), 'GraphicsRules.sgr': fileHash(join(BIN, 'GraphicsRules.sgr')) };
  for (const f of Object.values(state)) for (const x of f.files) if (!x.path.endsWith('Options.ini')) files[x.path.replace(BIN, 'Bin').replace(USER, 'User')] = fileHash(x.path);
  const pkgDir = join(USER, 'Mods', 'Packages');
  const mods = existsSync(pkgDir) ? readdirSync(pkgDir, { recursive: true }).map(String).filter(n => /\.package$/i.test(n))
    .map(n => `${n}:${n.startsWith('ts3') ? fileHash(join(pkgDir, n)) : statSync(join(pkgDir, n)).size}`).sort() : [];
  const options: Record<string, string> = {};
  const ini = join(USER, 'Options.ini');
  if (existsSync(ini)) for (const m of readFileSync(ini, 'latin1').matchAll(/^(\w*quality|enable\w+)\s*=\s*(\S+)/gim)) options[m[1].toLowerCase()] = m[2];
  const body = { git, fixes: Object.entries(state).map(([id, f]) => `${id}@${f.version ?? '-'}`).sort(), files, mods, options };
  return { at: new Date().toISOString(), key: sha(JSON.stringify(body)), ...(note ? { note } : {}), ...body };
}

const readLedger = (): Entry[] => (existsSync(LEDGER) ? readFileSync(LEDGER, 'utf8').split('\n').filter(Boolean).map(l => JSON.parse(l)) : []);

function diff(a: Entry | undefined, b: Entry): string[] {
  if (!a) return ['(first recorded change set)'];
  const out: string[] = [];
  if (a.git !== b.git) out.push(`git ${a.git} -> ${b.git}`);
  for (const x of b.fixes.filter(f => !a.fixes.includes(f))) out.push(`+fix ${x}`);
  for (const x of a.fixes.filter(f => !b.fixes.includes(f))) out.push(`-fix ${x}`);
  for (const k of new Set([...Object.keys(a.files), ...Object.keys(b.files)])) if (a.files[k] !== b.files[k]) out.push(`file ${k} ${a.files[k] ?? 'none'} -> ${b.files[k] ?? 'none'}`);
  for (const x of b.mods.filter(m => !a.mods.includes(m))) out.push(`+mod ${x}`);
  for (const x of a.mods.filter(m => !b.mods.includes(m))) out.push(`-mod ${x}`);
  for (const k of new Set([...Object.keys(a.options), ...Object.keys(b.options)])) if (a.options[k] !== b.options[k]) out.push(`option ${k} ${a.options[k]} -> ${b.options[k]}`);
  return out;
}

function snapshot(note?: string) {
  const e = current(note);
  const last = readLedger().at(-1);
  if (last?.key === e.key && !note) { console.log(`change set unchanged (${e.key})`); return; }
  appendFileSync(LEDGER, JSON.stringify(e) + '\n');
  console.log(`change set ${e.key} recorded; since last: ${diff(last, e).join('; ') || 'nothing (note only)'}`);
}

function triage(since?: string) {
  const ledger = readLedger();
  const reports = readdirSync(USER).filter(n => /^xcpt .*\.txt$/i.test(n) && (!since || statSync(join(USER, n)).mtime >= new Date(since))).sort();
  if (!reports.length) { console.log(since ? `no crash report written since ${since}` : 'no crash reports'); return; }
  for (const n of reports) {
    const t = readFileSync(join(USER, n), 'latin1');
    const [d, tm] = [t.match(/^date: (\S+)/m)?.[1], t.match(/^time: (\S+)/m)?.[1]];
    const when = d && tm ? new Date(`${d}T${tm.replace(/\./g, ':')}`) : statSync(join(USER, n)).mtime; // report time is local
    const type = t.match(/^type: (.+)$/m)?.[1]?.trim() ?? '?';
    const addr = t.match(/^address: (0x[0-9a-f]+) "([^"]*)"/im);
    const mod = addr ? `${addr[1]} in ${addr[2].split('\\').at(-1)}` : '?';
    const stackMods = [...new Set([...(t.split('[Call stack]')[1]?.split('[Stack data]')[0] ?? '').matchAll(/\\([^\\\r\n]+\.(?:exe|dll|asi))\r?\n/gi)].map(m => m[1]))];
    const i = ledger.findLastIndex(e => new Date(e.at) <= when);
    console.log(`\n${n}\n  ${when.toISOString()}  ${type}  at ${mod}  stack: ${stackMods.join(', ') || '-'}`);
    if (i < 0) { console.log('  change set: none recorded before this crash'); continue; }
    console.log(`  change set ${ledger[i].key} (recorded ${ledger[i].at}${ledger[i].note ? `, ${ledger[i].note}` : ''})`);
    console.log(`  changed just before: ${diff(ledger[i - 1], ledger[i]).join('; ')}`);
    const same = ledger.slice(0, i).filter(e => e.key === ledger[i].key).length;
    if (same) console.log(`  same change set was active ${same} earlier time(s)`);
  }
}

const { values, positionals } = parseArgs({ allowPositionals: true, options: { note: { type: 'string' }, since: { type: 'string' } } });
if (positionals[0] === 'snapshot') snapshot(values.note);
else if (positionals[0] === 'triage') triage(values.since);
else { console.error('usage: node tools/harness/crash-ledger.ts snapshot [--note "why"] | triage [--since <iso>]'); process.exit(2); }
