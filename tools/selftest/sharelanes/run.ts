// Offline self-check of the game-independent rules of sharelanes / laddershare / simpass / angleentry
// (mods/ts3fixes/src/ShareLanesCore.cs, docs/686 §8). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/sharelanes/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'sharelanes-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ShareLanesTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'ShareLanesCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
