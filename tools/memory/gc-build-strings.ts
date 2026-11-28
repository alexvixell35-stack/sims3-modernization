// docs/542 §1 S1 check on any TS3W.exe build (§9 gap row 1 "EA 1.69 the same build?"): the Mono version string and the Boehm
// GC strings that tell 6.x from 7.x and show what was compiled in. Reads the exe file only (no game, no process).
//   node tools/memory/gc-build-strings.ts <path to TS3W.exe> [...]   one line per string: present / absent, then the verdict
//   node tools/memory/gc-build-strings.ts --self-check
// Verdict 'same S1 as Steam 1.67' when every string's state equals the Steam 1.67 column of docs/542 §1 [verified there].
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// [string, present in Steam 1.67 (docs/542 §1)]
export const S1: [string, boolean][] = [
  ['Mono 1.2.3.1', true],
  ['GC Warning: Out of Memory!  Returning NIL!', true],
  ['Returning NULL', false],
  ['GC_PAUSE_TIME_TARGET', true], ['GC_LARGE_ALLOC_WARN_INTERVAL', true], ['GC_DUMP_REGULARLY', true], ['GC_NO_BLACKLIST_WARNING', true],
  ['GC_MAXIMUM_HEAP_SIZE', true], ['GC_INITIAL_HEAP_SIZE', true], ['GC_ENABLE_INCREMENTAL', true], ['GC_PRINT_STATS', true],
  ['GC_MARKERS', false], ['GC_NPROCS', false], ['GC_UNMAP_THRESHOLD', false], ['GC_FORCE_UNMAP', false], ['GC_FULL_FREQUENCY', false],
  ['GC_FREE_SPACE_DIVISOR', false],
  ['GC_gww_read_dirty unexpectedly failed', true],
];
export function check(exe: Buffer) {
  const rows = S1.map(([s, steam]) => ({ s, steam, here: exe.includes(Buffer.from(s, 'latin1')) }));
  return { rows, same: rows.every(r => r.here === r.steam) };
}

if (process.argv.includes('--self-check')) {
  const steamLike = Buffer.from(S1.filter(([, p]) => p).map(([s]) => `\0${s}\0`).join('junk'), 'latin1');
  assert.equal(check(steamLike).same, true);
  const v7 = Buffer.concat([steamLike, Buffer.from('\0GC_MARKERS\0', 'latin1')]);
  const r = check(v7);
  assert.equal(r.same, false);
  assert.deepEqual(r.rows.filter(x => x.here !== x.steam).map(x => x.s), ['GC_MARKERS']);
  console.log('gc-build-strings self-check ok');
} else {
  const files = process.argv.slice(2);
  if (!files.length) { console.error('usage: node tools/memory/gc-build-strings.ts <TS3W.exe> [...] | --self-check'); process.exit(2); }
  for (const f of files) {
    const { rows, same } = check(readFileSync(f));
    console.log(`\n${f}`);
    for (const r of rows) console.log(`  ${r.here ? 'present' : 'absent '}  ${r.here === r.steam ? '' : '(Steam 1.67: ' + (r.steam ? 'present' : 'absent') + ')  '}${r.s}`);
    console.log(same ? '  verdict: same S1 as Steam 1.67 (Mono 1.2.3.1 + Boehm 6.6 with EA GetWriteWatch)' : '  verdict: DIFFERS from Steam 1.67: read the rows marked above');
  }
}
