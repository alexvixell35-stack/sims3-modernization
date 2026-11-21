// Offline self-check of socialunlinkend (mods/ts3fixes/src/SocialUnlinkEndCore.cs, docs/650 §3): only a Receiver's
// Routed / Committed wait in an unlinked SocialInteractionB whose partner neither syncs with nor aims at it ends; exempt
// receivers are counted, never ended. Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/socialunlinkend/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'socialunlinkend-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'SocialUnlinkEndTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'SocialUnlinkEndCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
