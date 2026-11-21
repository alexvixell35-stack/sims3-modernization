// Offline self-check of caspropagateoutfits (mods/ts3fixes/src/CasPropagateCore.cs, docs/658 §6.1): EA's early return,
// the drop set with its protected outfits, index realignment.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/caspropagateoutfits/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'caspropagateoutfits-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'CasPropagateOutfitsTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'CasPropagateCore.cs'),
  join(ROOT, 'mods', 'ts3fixes', 'src', 'LineCapCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
