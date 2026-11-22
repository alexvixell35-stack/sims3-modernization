// Offline self-check of the game-independent rules of reachuse / reachusehh
// (mods/ts3fixes/src/ReachUseCore.cs, docs/686 §12). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/reachuse/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'reachuse-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ReachUseTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'ReachUseCore.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'BlockedUseCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
