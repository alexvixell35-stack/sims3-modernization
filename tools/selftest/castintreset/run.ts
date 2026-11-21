// Offline self-check of castintreset (mods/ts3fixes/src/CasSmallCores.cs CasTintResetCore, docs/658 §6.6; unconfirmed).
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/castintreset/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'castintreset-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'CasTintResetTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'CasSmallCores.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
