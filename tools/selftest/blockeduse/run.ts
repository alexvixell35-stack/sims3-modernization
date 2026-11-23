// Offline self-check of the game-independent rules of blockeduse / blockedusehh
// (mods/ts3fixes/src/BlockedUseCore.cs, docs/686 §10). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/blockeduse/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'blockeduse-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'BlockedUseTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'BlockedUseCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
