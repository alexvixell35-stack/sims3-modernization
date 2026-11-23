// Offline self-check of the game-independent part of horseshoesolo (mods/ts3fixes/src/HorseshoeSoloCore.cs, docs/865).
// Windows (.NET Framework csc).
//   node tools/selftest/horseshoesolo/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'horseshoesolo-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'HorseshoeSoloTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'HorseshoeSoloCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
