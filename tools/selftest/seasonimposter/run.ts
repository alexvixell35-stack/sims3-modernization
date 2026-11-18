// Offline self-check of seasonimposter's lot queue (mods/ts3fixes/src/SeasonImposterCore.cs, issue #82): each lot queued
// once, warm lots first in queue order, cold lots nearest first only when allowed, every lot handed out exactly once.
// Windows (.NET Framework csc).
//   node tools/selftest/seasonimposter/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'seasonimposter-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'SeasonImposterTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'SeasonImposterCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
