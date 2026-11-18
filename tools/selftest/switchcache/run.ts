// Offline self-check of the ShortcutOn answer cache (mods/ts3fixes/src/SwitchCacheCore.cs, docs/605 §2): reference hits,
// same-text misses, the half-full cap, null / empty names, and the probe lengths of every real switch name (the
// ShortcutOn literals and the case labels of the slow path, read from the source).
//   node tools/selftest/switchcache/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const names = new Set<string>();
for (const f of readdirSync(SRC).filter((f) => f.endsWith('.cs'))) {
  const t = readFileSync(join(SRC, f), 'utf8');
  for (const m of t.matchAll(/ShortcutOn\("(\w+)"\)/g)) names.add(m[1]);
  if (f === 'Fixes.cs') for (const m of t.matchAll(/case "(\w+)":/g)) names.add(m[1]);
}
const list = join(out, 'switchcache-names.txt');
writeFileSync(list, [...names].sort().join('\n') + '\n');
const exe = join(out, 'switchcache-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'SwitchCacheTest.cs'), join(SRC, 'SwitchCacheCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [list], { stdio: 'inherit' });
