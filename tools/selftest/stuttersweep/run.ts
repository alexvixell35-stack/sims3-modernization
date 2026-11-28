// Offline self-check of the docs/662 stutter sweep fixes (mods/ts3fixes/src/StutterSweepCore.cs).
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/stuttersweep/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'stuttersweep-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'StutterSweepTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'StutterSweepCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
