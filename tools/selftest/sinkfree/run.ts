// Offline self-check of the game-independent rules of sinkfree
// (mods/ts3fixes/src/SinkFreeCore.cs, docs/686 §40). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/sinkfree/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'sinkfree-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'SinkFreeTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'SinkFreeCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
