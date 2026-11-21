// Offline self-check of shuffleboardnpccap (mods/ts3fixes/src/ShuffleboardNpcCapCore.cs, docs/647): round, age and night caps,
// streak gaps, the re-join cooldown and the bounded tables.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/shuffleboardnpccap/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'shuffleboardnpccap-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ShuffleboardNpcCapTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'ShuffleboardNpcCapCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
