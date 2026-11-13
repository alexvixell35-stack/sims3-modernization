// Release defaults check: a feature that shipped ON in the previous release must still be ON in master's defaults, or the next
// release built from master silently drops it (v0.2.11/v0.2.12 shipped ten ts3fixes switches and three ini keys through hand-made
// release files while master stayed off). Compares the previous release's shipped ts3native.ini and ts3fixes.package (its
// tuning XML is stored uncompressed) with master's defaults: native/ts3native/ts3native.ini, the tuning XML in
// tools/setup/build-fixes.ts and the [Tunable] defaults in mods/ts3fixes/src/Instantiator.cs.
// A key is ON in the release when its ini value is a non-zero number or its tuning value is True. It FAILs when master's value is
// not the released one (ini "Section.Key", tuning "kName") unless docs/data/release-default-exceptions.json lists the key with a reason.
//   node tools/release-defaults-check.ts --prev-ini <shipped ts3native.ini> --prev-pkg <shipped ts3fixes.package>
//   node tools/release-defaults-check.ts --self-check
// Runs in the publish-public.ts release dry run (--prev-ini/--prev-pkg of the previous release's zip and package).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';

const ROOT = resolve(import.meta.dirname, '..');

export function parseIni(text: string): Record<string, string> {
  const o: Record<string, string> = {};
  let s = '';
  for (const raw of text.split(/\r?\n/)) {
    const l = raw.trim(); let m;
    if ((m = l.match(/^\[(\w+)\]/))) s = m[1];
    else if ((m = l.match(/^(\w+)\s*=\s*([^;#]*)/))) o[`${s}.${m[1]}`] = m[2].trim();
  }
  return o;
}
// <kName value="True" /> in a tuning XML (build-fixes.ts template or the xml bytes inside a .package)
export function parseTuning(text: string): Record<string, string> {
  const o: Record<string, string> = {};
  for (const m of text.matchAll(/<(k\w+) value="([^"]*)"/g)) o[m[1]] = m[2];
  return o;
}
// [Tunable] public static bool kName = true;  (bool tunables only)
export function parseDefaults(cs: string): Record<string, string> {
  const o: Record<string, string> = {};
  for (const m of cs.matchAll(/\[Tunable\]\s+public static bool (k\w+)\s*=\s*(true|false)\s*;/g)) o[m[1]] = m[2] === 'true' ? 'True' : 'False';
  return o;
}
const iniOn = (v: string) => /^-?\d+(\.\d+)?$/.test(v) && Number(v) !== 0;

// released = shipped ini + tuning; master = ini + tuning xml + Instantiator defaults. Returns one line per dropped key.
export function compare(rel: { ini: Record<string, string>; tuning: Record<string, string> },
  master: { ini: Record<string, string>; tuning: Record<string, string>; defaults: Record<string, string> }, allow: Set<string>): string[] {
  const bad: string[] = [];
  for (const [k, v] of Object.entries(rel.ini)) if (iniOn(v) && master.ini[k] !== v && !allow.has(k)) bad.push(`ini ${k}: released ${v}, master ${master.ini[k] ?? '(missing)'}`);
  for (const [k, v] of Object.entries(rel.tuning)) {
    if (v !== 'True' || allow.has(k)) continue;
    const x = master.tuning[k] ?? master.defaults[k]; // the XML entry wins over the Instantiator.cs default
    if (x !== 'True') bad.push(`tuning ${k}: released True, master ${master.tuning[k] !== undefined ? 'build-fixes.ts' : 'Instantiator.cs'} ${x ?? '(missing)'}`);
  }
  return bad;
}
export function loadAllow(file: string): Set<string> {
  const j = JSON.parse(readFileSync(file, 'utf8')) as { exceptions: { key: string; reason: string }[] };
  for (const e of j.exceptions) if (!e.key || !e.reason?.trim()) throw new Error(`${file}: every exception needs key and reason`);
  return new Set(j.exceptions.map(e => e.key));
}
// returns the failure lines (empty = ok)
export function check(prevIni: string, prevPkg: string, root = ROOT): string[] {
  const rd = (f: string) => readFileSync(f).toString('latin1');
  const rel = { ini: parseIni(rd(prevIni)), tuning: parseTuning(rd(prevPkg)) };
  if (!Object.keys(rel.tuning).length) throw new Error(`${prevPkg}: no <kName value=...> tuning found (is it a ts3fixes.package?)`);
  const master = { ini: parseIni(rd(join(root, 'native/ts3native/ts3native.ini'))), tuning: parseTuning(rd(join(root, 'tools/setup/build-fixes.ts'))),
    defaults: parseDefaults(rd(join(root, 'mods/ts3fixes/src/Instantiator.cs'))) };
  return compare(rel, master, loadAllow(join(root, 'docs/data/release-default-exceptions.json')));
}

function selfCheck() {
  // real lines from the repo
  assert.deepEqual(parseIni('[LotPool]\r\n; c\r\nTrim=1\r\nSettleSec=15 ; s\r\n[AutShadow]\r\nRaces=4\r\n'), { 'LotPool.Trim': '1', 'LotPool.SettleSec': '15', 'AutShadow.Races': '4' });
  assert.deepEqual(parseIni('Enabled=1\n[A]\nEnabled=0'), { '.Enabled': '1', 'A.Enabled': '0' });
  assert.deepEqual(parseTuning('    <kBookHand value="True" />\n    <kSnapshotCap value="0" />\n<base>'), { kBookHand: 'True', kSnapshotCap: '0' });
  assert.deepEqual(parseTuning('x\0<kWatchdogAsync value="False" />'), { kWatchdogAsync: 'False' });
  assert.deepEqual(parseDefaults('        [Tunable] public static bool kErrorTrapSims = false;       // opt-in\n        [Tunable] public static bool kBookHand = true;\n        [Tunable] public static int kSnapshotCap = 0;'), { kErrorTrapSims: 'False', kBookHand: 'True' });
  assert.deepEqual(parseDefaults('public static bool kPlain = true;'), {}, 'not a [Tunable]');
  const rel = { ini: { 'LotPool.Trim': '1', 'LotPool.SettleSec': '15', 'X.Off': '0', 'X.Mode': 'replay' }, tuning: { kBookHand: 'True', kOffHere: 'False', kSnapshotCap: '7' } };
  const same = { ini: { ...rel.ini }, tuning: { ...rel.tuning }, defaults: { kBookHand: 'True' } };
  assert.deepEqual(compare(rel, same, new Set()), [], 'identical = ok');
  // master may turn on more than the release
  assert.deepEqual(compare(rel, { ...same, ini: { ...rel.ini, 'X.Off': '1' }, tuning: { ...rel.tuning, kOffHere: 'True' } }, new Set()), []);
  const bad = compare(rel, { ini: { 'LotPool.Trim': '0', 'LotPool.SettleSec': '60' }, tuning: { kBookHand: 'False' }, defaults: { kBookHand: 'False' } }, new Set());
  assert.equal(bad.length, 3, bad.join('\n'));
  assert.ok(bad.some(b => b.includes('LotPool.Trim: released 1, master 0')) && bad.some(b => b.includes('SettleSec: released 15, master 60')));
  assert.ok(compare(rel, { ...same, tuning: {}, defaults: { kBookHand: 'False' } }, new Set()).some(b => b.includes('Instantiator.cs False')));
  assert.ok(compare(rel, { ini: {}, tuning: same.tuning, defaults: {} }, new Set()).some(b => b.includes('(missing)')), 'key missing in master fails');
  assert.deepEqual(compare(rel, { ini: { ...rel.ini, 'LotPool.Trim': '0' }, tuning: same.tuning, defaults: same.defaults }, new Set(['LotPool.Trim'])), [], 'allow-list');
  assert.throws(() => parseTuning('') && check('/nonexistent', '/nonexistent'));
  // the repo itself: its own allow-list parses
  loadAllow(join(ROOT, 'docs/data/release-default-exceptions.json'));
  console.log('self-check ok');
}

if (import.meta.main) {
  const { values: a } = parseArgs({ options: { 'prev-ini': { type: 'string' }, 'prev-pkg': { type: 'string' }, 'self-check': { type: 'boolean' } } });
  if (a['self-check']) selfCheck();
  else if (a['prev-ini'] && a['prev-pkg']) {
    const bad = check(a['prev-ini'], a['prev-pkg']);
    if (bad.length) { console.error(`FAIL: ON in the previous release, not in master defaults (flip them, or list them with a reason in docs/data/release-default-exceptions.json):\n${bad.join('\n')}`); process.exit(1); }
    console.log('release defaults ok: everything ON in the previous release is ON in master');
  } else { console.error('usage: node tools/release-defaults-check.ts --prev-ini <ini> --prev-pkg <package> | --self-check'); process.exit(2); }
}
