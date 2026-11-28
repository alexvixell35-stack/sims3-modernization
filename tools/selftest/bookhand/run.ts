// Offline self-check of the decisions of bookhand (mods/ts3fixes/src/BookHandCore.cs): when a book left in the hand with no
// carry machine is stuck, when an EA put-down would throw, what the hook does, when the diag check logs.
// Windows (.NET Framework csc).
//   node tools/selftest/bookhand/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'bookhand-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'BookHandTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'BookHandCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
