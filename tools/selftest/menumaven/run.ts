// Offline self-check of the Menu Maven recipe rules (mods/ts3fixes/src/MenuMavenCore.cs, docs/501): which recipes count
// and when a Sim knows them all.
// Windows (.NET Framework csc).
//   node tools/selftest/menumaven/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'menumaven-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'MenuMavenTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'MenuMavenCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
