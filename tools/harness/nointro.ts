// Builds build/ts3-nointro.package: overrides the startup videos with zero-frame VP6 stubs so the game skips them.
// Same technique as the community "No Intro" mods (instances taken from what they override); the stub bytes are ours.
// Usage: node tools/harness/nointro.ts
import { mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { TYPE_VIDEO, writePackage } from '../dbpf.ts';

// Video resources played before the main menu (EA, Maxis and EP logos/intros), type 0xB1CC1AF6, group 0.
const INTRO_VIDEOS = [
  0x799ae80054cee156n, 0xb50331a5f69e749an, 0x560fb403c71622een, 0x4e5d8b03c2ff9dfcn, 0x5d8e65e8e531012bn,
  0x5d9260e8e534e1d0n, 0x5d956ae8e53728e9n, 0x5d9865e8e5395596n, 0x5dbe74e8e55a3ef0n, 0x5d655fe8e50df26bn,
  0x5d706ae8e5180c2en, 0xa07accbd24b4db32n, 0xad46c72ab29acf48n,
  0x7a9a9bc88ec40774n, // Maxis logo
];

// EA VP6 container header chunk with no frames.
export function emptyVp6(): Buffer {
  const h = Buffer.alloc(32);
  h.write('MVhd', 0, 'latin1');
  h.writeUInt32LE(32, 4);         // chunk size
  h.write('06PV', 8, 'latin1');   // codec "VP60" as a little-endian FourCC
  h.writeUInt16LE(256, 12);       // width
  h.writeUInt16LE(256, 14);       // height
  h.writeUInt32LE(0, 16);         // frame count: nothing to play
  h.writeUInt32LE(0, 20);         // largest frame
  h.writeUInt32LE(15000, 24);     // rate
  h.writeUInt32LE(1000, 28);      // scale: 15 fps
  return h;
}

if (process.argv[1]?.endsWith('nointro.ts')) {
  const stub = emptyVp6();
  console.assert(stub.toString('hex') === '4d5668642000000030365056000100010000000000000000983a0000e8030000', 'stub header layout');
  // no process.exit here: on Node 24 it can abort with a libuv UV_HANDLE_CLOSING assertion (exit code != 0) after "ok"
  if (process.argv.includes('--self-check')) console.log('nointro self-check ok');
  else buildPackage(stub);
}

function buildPackage(stub: Buffer) {
  const out = join(resolve(import.meta.dirname, '..', '..'), 'build');
  mkdirSync(out, { recursive: true });
  const pkg = writePackage(INTRO_VIDEOS.map(instance => ({ type: TYPE_VIDEO, group: 0, instance, data: stub })));
  writeFileSync(join(out, 'ts3-nointro.package'), pkg);
  console.log(`built ${join(out, 'ts3-nointro.package')} (${INTRO_VIDEOS.length} videos stubbed)`);
}
