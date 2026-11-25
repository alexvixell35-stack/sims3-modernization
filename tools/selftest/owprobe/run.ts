// Offline self-check of mods/ts3fixes/src/OverwatchProbeCore.cs (switch owprobeplans, docs/163 §9). Windows (.NET Framework csc).
//   node tools/selftest/owprobe/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'owprobe-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'OwProbeTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'OverwatchProbeCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
