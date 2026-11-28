// Offline self-check of the game-independent rules of the unseen-skip shortcuts
// (mods/ts3fixes/src/UnseenSkipCore.cs, docs/686 §37). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/unseenskip/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'unseenskip-test.exe');
const src = (name: string) => join(ROOT, 'mods', 'ts3fixes', 'src', name);
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'UnseenSkipTest.cs'), src("UnseenSkipCore.cs"), src("LineSwitchCore.cs")], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
