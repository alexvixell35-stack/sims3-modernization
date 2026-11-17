// Guard for the EA 1.69 address table (CI job native-ea169, /DTS3A_TABLE): new native code that uses a Steam 1.67 address
// with no row in native/ts3native/ts3addr-ea169.h fails there with C2975, and a wrong ts3addr_count hides rows. Fails locally first.
//   node tools/ts3addr-check.ts [root]      checks every TS3A(0x...) literal in the files ts3native.cpp includes (the files CI compiles)
//   node tools/ts3addr-check.ts --self-check
// A row with a 0 EA value is fine (unresolved). TS3A(<non-literal>) is not checkable here and is skipped.
// Runs in publish-public.ts before anything is written, and in selftest-all.
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..');
const TABLE = 'ts3addr-ea169.h';

const stripComments = (s: string) => s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
export const usedAddrs = (src: string): number[] => [...stripComments(src).matchAll(/\bTS3A\(\s*(0[xX][0-9A-Fa-f]+)/g)].map(m => parseInt(m[1], 16));
export const tableRows = (h: string): number[] => [...h.matchAll(/^\s*\{0[xX]([0-9A-Fa-f]+),\s*0[xX][0-9A-Fa-f]+\}/gm)].map(m => parseInt(m[1], 16));
export const tableCount = (h: string): number | undefined => { const m = h.match(/^constexpr int ts3addr_count = (\d+);/m); return m ? Number(m[1]) : undefined; };
export const includes = (src: string): string[] => [...src.matchAll(/^\s*#\s*include\s+"([^"]+)"/gm)].map(m => m[1]);

export function problems(uses: Map<number, string>, header: string): string[] {
  const out: string[] = [];
  const rows = tableRows(header), have = new Set(rows), n = tableCount(header);
  if (n === undefined) out.push(`${TABLE}: no "constexpr int ts3addr_count = N;" line`);
  else if (n !== rows.length) out.push(`${TABLE}: ts3addr_count ${n} but ${rows.length} rows (the last rows are hidden or the lookup reads past the table)`);
  if (rows.some((r, i) => i && rows[i - 1] >= r)) out.push(`${TABLE}: rows not sorted by Steam address (binary search fails)`);
  for (const [a, f] of uses) if (!have.has(a)) out.push(`0x${a.toString(16).toUpperCase().padStart(8, '0')} used in ${f} has no row in ${TABLE} (add a row, 0 EA value if unresolved; C2975 in CI otherwise)`);
  return out;
}

export function check(root = ROOT): string[] {
  const d = join(root, 'native/ts3native'), seen = new Set<string>(), q = ['ts3native.cpp'], uses = new Map<number, string>();
  while (q.length) {
    const f = q.pop()!;
    if (seen.has(f)) continue;
    seen.add(f);
    if (!existsSync(join(d, f))) continue; // system / generated header
    const src = readFileSync(join(d, f), 'latin1');
    if (f !== TABLE) for (const a of usedAddrs(src)) if (!uses.has(a)) uses.set(a, f);
    q.push(...includes(src));
  }
  if (!seen.has('ts3native.cpp') || !existsSync(join(d, TABLE))) return [`native/ts3native/ts3native.cpp or ${TABLE} missing`];
  return problems(uses, readFileSync(join(d, TABLE), 'latin1'));
}

function selfCheck() {
  // real lines from the tree: a use in a table-mode file, and header rows / count
  assert.deepEqual(usedAddrs('g_x = (const BYTE*)(uintptr_t)TS3A(0x00E5637D), y = TS3A(0x01220EC0);'), [0xE5637D, 0x1220EC0]);
  assert.deepEqual(usedAddrs('#define TS3A(x) ((decltype(x))ts3addr_c<ts3addr_find((x), 0, ts3addr_count - 1)>::v)'), [], 'macro definition: not a literal');
  assert.deepEqual(usedAddrs('// TS3A(0x00401000) in a comment\n/* TS3A(0x00401BE0) */ int a;'), [], 'comments ignored');
  assert.deepEqual(usedAddrs('TS3A(base + 0x10)'), [], 'computed arg skipped');
  assert.deepEqual(tableRows('    {0x00401000, 0x00401000},\n    {0x0044C470, 0x00000000},\n// unresolved: 0x004D9AD0 no-ea'), [0x401000, 0x44C470]);
  assert.deepEqual(tableRows('constexpr unsigned long ts3addr_pairs[][2] = {'), []);
  assert.equal(tableCount('constexpr int ts3addr_count = 1457;'), 1457);
  assert.equal(tableCount('// constexpr int ts3addr_count = 3;'), undefined, 'commented count');
  assert.deepEqual(includes('#include "ts3addr.h"\n  # include "a-b.cpp"\n#include <windows.h>'), ['ts3addr.h', 'a-b.cpp']);
  const h = (rows: string, n: number) => `${rows}\nconstexpr int ts3addr_count = ${n};\n`;
  const two = '    {0x00401000, 0x00401000},\n    {0x0044C470, 0x00000000},';
  const u = (...a: number[]) => new Map(a.map(x => [x, 'f.cpp'] as [number, string]));
  assert.deepEqual(problems(u(0x401000, 0x44C470), h(two, 2)), [], 'ok, 0 row allowed');
  assert.match(problems(u(0x401001), h(two, 2)).join(), /0x00401001 used in f\.cpp has no row/);
  assert.match(problems(u(), h(two, 1)).join(), /ts3addr_count 1 but 2 rows/);
  assert.match(problems(u(), h(two, 3)).join(), /ts3addr_count 3 but 2 rows/);
  assert.match(problems(u(), two).join(), /no "constexpr int ts3addr_count/);
  assert.match(problems(u(), h('    {0x0044C470, 0x0},\n    {0x00401000, 0x1},', 2)).join(), /not sorted/);
  const bad = check(); // the repo itself
  assert.deepEqual(bad, [], bad.join('\n'));
  console.log('self-check ok');
}

if (import.meta.main) {
  if (process.argv.includes('--self-check')) selfCheck();
  else {
    const bad = check(process.argv[2] ? resolve(process.argv[2]) : ROOT);
    if (bad.length) { console.error(`FAIL: EA 1.69 address table:\n${bad.join('\n')}`); process.exit(1); }
    console.log('ts3addr check ok: every TS3A(0x...) has a row, ts3addr_count matches');
  }
}
