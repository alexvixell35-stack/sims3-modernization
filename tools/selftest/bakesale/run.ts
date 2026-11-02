// Offline self-check of the bake sale seller fix decisions (mods/ts3fixes/src/BakeSaleCore.cs, issue #102): a deleted seller
// reads as no seller, an orphaned NPC table gets its clean-up alarm back.
// Windows (.NET Framework csc).
//   node tools/selftest/bakesale/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'bakesale-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'BakeSaleTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'BakeSaleCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
