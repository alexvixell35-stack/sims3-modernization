// Offline self-check of tool_drag's world -> pixel projection math (mods/ts3agent/src/plugin/ScreenFit.cs).
// Windows (.NET Framework csc).
//   node tools/selftest/screenfit/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'screenfit-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ScreenFitTest.cs'), join(ROOT, 'mods', 'ts3agent', 'src', 'plugin', 'ScreenFit.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
