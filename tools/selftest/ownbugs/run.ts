// Offline self-check of the docs/651 own-bug rules (mods/ts3fixes/src/OwnBugsCore.cs).
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/ownbugs/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'ownbugs-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'OwnBugsTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'OwnBugsCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
