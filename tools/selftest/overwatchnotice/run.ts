// Offline self-check of the game-independent parts of the Overwatch stuck-notice summary (mods/ts3fixes/src/
// OverwatchNoticeCore.cs, docs/145 §13): window open/close, one count per Sim, the summary text.
// Windows (.NET Framework csc).
//   node tools/selftest/overwatchnotice/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'overwatchnotice-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'OverwatchNoticeTest.cs'), join(SRC, 'OverwatchNoticeCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
