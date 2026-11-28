// Offline self-check of the game-independent parts of the unseen wake-up switches (mods/ts3fixes/src/WakeParkCore.cs,
// docs/211). Windows (.NET Framework csc).
//   node tools/selftest/wakepark/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'wakepark-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'WakeParkTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'WakeParkCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
