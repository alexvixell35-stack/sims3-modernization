// Offline self-check of unseenboatwide (mods/ts3fixes/src/UnseenBoatWideCore.cs, docs/248 §12): party, child, owned-boat,
// colour-combine, goal-kind and counter rules.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/unseenboatwide/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'unseenboatwide-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'UnseenBoatWideTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'UnseenBoatWideCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
