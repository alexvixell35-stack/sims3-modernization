// Offline self-check of picnicexit (mods/ts3fixes/src/PicnicExitCore.cs, docs/645 §1): which finished
// picnic situations are exited (no double exit, no Sims, no queued cleanup).
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/picnicexit/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'picnicexit-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'PicnicExitTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'PicnicExitCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
