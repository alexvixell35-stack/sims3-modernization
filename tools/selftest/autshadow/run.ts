// Offline self-check of the [AutShadow] compare rules (mods/ts3fixes/src/AutShadowCore.cs, docs/122 §5).
// Windows (.NET Framework csc).
//   node tools/selftest/autshadow/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'autshadow-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'AutShadowTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'AutShadowCore.cs')], { stdio: 'inherit' });
const res = execFileSync(exe, [], { encoding: 'utf8' }).split(/\r?\n/);
console.log(res.filter((l) => l && !l.startsWith('ilhash ')).join('\n'));
// G-M6 e2e: the generator's names from metadata must give the hashes the runtime side (reflection) gave
const want = new Map(res.filter((l) => l.startsWith('ilhash ')).map((l) => { const s = l.lastIndexOf(' '); return [l.slice(7, s), l.slice(s + 1)]; }));
const got = execFileSync(process.execPath, [join(ROOT, 'tools', 'autshadow-il-hashes.ts'), '--hash-file', exe, ...want.keys()], { encoding: 'utf8' });
let bad = 0;
for (const l of got.trim().split(/\r?\n/)) {
  const s = l.lastIndexOf(' '), k = l.slice(0, s), h = l.slice(s + 1);
  if (want.get(k) !== h) { bad++; console.log(`FAIL: G-M6 e2e ${k}: runtime ${want.get(k)}, generator ${h}`); }
}
const ok = !bad && want.size === 3;
console.log(`autshadow G-M6 e2e: ${ok ? 'PASS' : 'FAIL'} (${want.size} methods)`);
if (!ok) process.exit(1);
