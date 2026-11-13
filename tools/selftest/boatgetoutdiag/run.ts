// Offline self-check of the boatgetoutdiag watch table (mods/ts3fixes/src/BoatGetOutDiagCore.cs, docs/257 §7): push/pop,
// the own task's normal end, foreign and nested writers, overflow; and the boatgetoutguard decision (§9). Then the guard
// lines the C# test prints (the real format string) against the regexes of tools/harness/queues/boat-getout-stress.json:
// each guard regex must match its own scope's line and not the other one. Windows (.NET Framework csc).
//   node tools/selftest/boatgetoutdiag/run.ts
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'boatgetoutdiag-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'BoatGetOutDiagTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'BoatGetOutDiagCore.cs')], { stdio: 'inherit' });
const text = execFileSync(exe, []).toString();
process.stdout.write(text.split(/\r?\n/).filter(l => !l.startsWith('LINE ')).join('\n'));
const lines = text.split(/\r?\n/).filter(l => l.startsWith('LINE ')).map(l => l.slice(5));
const npc = lines.find(l => / npc /.test(l)), hh = lines.find(l => / household /.test(l));
assert.ok(npc && hh, 'the C# test prints one npc and one household guard line');
const q = JSON.parse(readFileSync(join(ROOT, 'tools', 'harness', 'queues', 'boat-getout-stress.json'), 'utf8'));
const keys = new Set<string>();
for (const j of q.jobs) for (const k of [...Object.keys(j.logRequire ?? {}), ...Object.keys(j.logForbid ?? {})]) if (k.startsWith('boatgetout guard')) keys.add(k);
assert.equal(keys.size, 3, 'queue guard regexes: npc, household, any');
for (const k of keys) {
  const re = new RegExp(k);
  if (k.includes(' npc ')) { assert.ok(re.test(npc!), `${k} must match ${npc}`); assert.ok(!re.test(hh!), `${k} must not match ${hh}`); }
  else if (k.includes(' household ')) { assert.ok(re.test(hh!), `${k} must match ${hh}`); assert.ok(!re.test(npc!), `${k} must not match ${npc}`); }
  else { assert.ok(re.test(npc!) && re.test(hh!), `${k} must match both`); assert.ok(!re.test('boatgetout stale: Mylee Arias npc NRE in GetOut after replaced #1'), `${k} must not match a stale line`); }
}
console.log('\nboatgetoutguard queue regexes vs real format: ok');
