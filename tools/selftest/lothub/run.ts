// Offline self-check of the managed lot hub (mods/ts3fixes/src/LotHubCore.cs, docs/321 §9): fake native queue,
// dispatch order and once per event. Windows (.NET Framework csc).
//   node tools/selftest/lothub/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'lothub-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'LotHubTest.cs'), join(SRC, 'LotHubCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
