// Offline self-check of the decision of bookcarry (mods/ts3fixes/src/BookCarryCore.cs): when Book.StopUsingBook
// dropped the carry state machine.
// Windows (.NET Framework csc).
//   node tools/selftest/bookcarry/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'bookcarry-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'BookCarryTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'BookCarryCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
