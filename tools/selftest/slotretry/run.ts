// Offline self-check of the game-independent rules of slotretry
// (mods/ts3fixes/src/SlotRetryCore.cs, docs/686). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/slotretry/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'slotretry-test.exe');
const src = (name: string) => join(ROOT, 'mods', 'ts3fixes', 'src', name);
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'SlotRetryTest.cs'), src('SlotRetryCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
