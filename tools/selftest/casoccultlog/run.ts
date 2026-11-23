// Offline self-check of casoccultlog (mods/ts3fixes/src/CasSmallCores.cs CasOccultLogCore, docs/658 §6.4) and the log cap.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/casoccultlog/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'casoccultlog-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'CasOccultLogTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'CasSmallCores.cs'),
  join(ROOT, 'mods', 'ts3fixes', 'src', 'LineCapCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
