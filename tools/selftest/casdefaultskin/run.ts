// Offline self-check of casdefaultskin (mods/ts3fixes/src/CasDefaultSkinCore.cs, docs/658 §6.3): the default ramp pick
// no longer depends on package order.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/casdefaultskin/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'casdefaultskin-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'CasDefaultSkinTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'CasDefaultSkinCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
