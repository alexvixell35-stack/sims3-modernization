// Offline self-check of caspropagateextra (mods/ts3fixes/src/CasPropagateCore.cs, docs/658 §6.2): which outfits get the
// extra propagation.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/caspropagateextra/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'caspropagateextra-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'CasPropagateExtraTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'CasPropagateCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
