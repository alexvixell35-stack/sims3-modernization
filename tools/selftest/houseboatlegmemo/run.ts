// Offline self-check of houseboatlegmemo (mods/ts3fixes/src/HouseboatLegMemoCore.cs, docs/857 §2): hit only from the same
// router, start and goal inside the window; expiry, clock going back, replacement and the bounded table.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/houseboatlegmemo/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'houseboatlegmemo-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'HouseboatLegMemoTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'HouseboatLegMemoCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
