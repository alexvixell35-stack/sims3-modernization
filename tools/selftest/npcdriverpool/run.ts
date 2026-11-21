// Offline self-check of the decision of npcdriverpool (mods/ts3fixes/src/NpcDriverPoolCore.cs, issue #166 build 37): which pooled driver
// descriptions are dropped.
// Windows (.NET Framework csc).
//   node tools/selftest/npcdriverpool/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'npcdriverpool-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'NpcDriverPoolTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'NpcDriverPoolCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
