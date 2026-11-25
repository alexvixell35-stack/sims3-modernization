// Adaptive diag kit (docs/316 §7): the expected-install list for one edition, from a clean kit start log.
//   node tools/adaptdiag-expect.ts <ts3native.log> [--edition steam167|retail167|ea169] > tools/player/ts3-kit/payload/adaptdiag/expect-<edition>.txt
//   node tools/adaptdiag-expect.ts --self-check
// One row per "OK   " start line: its stable name (text before the first ':', '(', '[', ' at 0x', ' = ' or digit run), which
// ts3native's install check finds by substring in the next start's OK / SKIP / FAIL lines. ts3fixes "adaptinstall:" failed
// patches of the reference start become "allow:" rows (an edition that cannot apply them is not "missing" them).
import { readFileSync } from 'node:fs';

const RX = {
  ok: /^OK {3}(.+)$/,
  cut: /\s*(?::|\(|\[| at 0x| = |\b0x[0-9A-Fa-f]+|\b\d+\b).*$/,
  fixFail: /^ts3fixes: adaptinstall: patched \d+, failed \d+: (.*)$/,
  fixName: /^\s*([A-Za-z_][\w.+]*\.[A-Za-z_]\w*)\s*:/,
};

export function okName(line: string): string | null {
  const m = RX.ok.exec(line.trimEnd());
  if (!m) return null;
  const n = m[1].replace(RX.cut, '').trim();
  return n.length >= 4 ? n : null;   // too short to find by substring without false hits
}

export function allowNames(line: string): string[] {
  const m = RX.fixFail.exec(line.trimEnd());
  if (!m || !m[1].trim()) return [];
  return m[1].split(';').map((p) => RX.fixName.exec(p)?.[1]).filter((x): x is string => !!x);
}

export function expectList(log: string, edition: string): string {
  const names = new Set<string>(), allow = new Set<string>();
  for (const l of log.split(/\r?\n/)) {
    const n = okName(l); if (n) names.add(n);
    for (const a of allowNames(l)) allow.add(a);
  }
  return [`# ts3native adaptive diag kit: expected install parts, ${edition} (docs/316 §7), from tools/adaptdiag-expect.ts`,
    ...[...names].sort(), ...[...allow].sort().map((a) => `allow: ${a}`)].join('\r\n') + '\r\n';
}

function selfCheck() {
  const eq = (a: unknown, b: unknown, what: string) => { if (JSON.stringify(a) !== JSON.stringify(b)) { console.error(`FAIL ${what}: ${JSON.stringify(a)} != ${JSON.stringify(b)}`); process.exit(1); } };
  // real lines from a ts3native.log start (2026-11-19)
  eq(okName('OK   GC growth step: 0x00E4A1D8 imm 0x800 -> 0x2000'), 'GC growth step', 'ok: name before the colon');
  eq(okName('OK   detour CasCompositorRegister at 0x005EECD0'), 'detour CasCompositorRegister', 'ok: name before " at 0x"');
  eq(okName('OK   abdiag ([Diag] AbHourly=1, docs/294): ts3fixes flips 34 native'), 'abdiag', 'ok: name before "("');
  eq(okName('OK   avoid RO A-site: 1 call sites'), 'avoid RO A-site', 'ok: hyphenated name');
  eq(okName('OK   0x833680 order sites (docs/89 §16): 2 call sites'), null, 'ok: a line that starts with an address has no stable name');
  eq(okName('SKIP pccheck ([PcCheck] Enabled=0)'), null, 'non-match: SKIP line');
  eq(okName('route async: mode 1 started 375 refused 8 collected 374'), null, 'non-match: stat line');
  eq(allowNames('ts3fixes: adaptinstall: patched 412, failed 2: Sims3.Gameplay.Foo.Bar: REFUSED body differs; Baz.Qux: not patched'), ['Sims3.Gameplay.Foo.Bar', 'Baz.Qux'], 'allow: failed patch names');
  eq(allowNames('ts3fixes: adaptinstall: patched 412, failed 0: '), [], 'allow: nothing failed');
  eq(allowNames('ts3fixes: ts3fixes 0.1.1 build 23e76546, 447 settings on/valued, 115 off'), [], 'non-match: version line');
  const out = expectList('OK   GC growth step: 0x1\r\nOK   GC growth step: 0x2\nSKIP x\nts3fixes: adaptinstall: patched 1, failed 1: A.B: REFUSED\n', 'steam167');
  eq(out.split('\r\n').slice(1), ['GC growth step', 'allow: A.B', ''], 'list: unique names, allow rows last, CRLF');
  console.log('adaptdiag-expect: self-check ok');
}

if (process.argv[1]?.endsWith('adaptdiag-expect.ts')) {
  const a = process.argv.slice(2);
  if (a.includes('--self-check')) { selfCheck(); process.exit(0); }
  if (!a[0]) { console.error('usage: node tools/adaptdiag-expect.ts <ts3native.log> [--edition steam167|retail167|ea169]'); process.exit(2); }
  const i = a.indexOf('--edition');
  process.stdout.write(expectList(readFileSync(a[0], 'latin1'), i >= 0 ? a[i + 1] : 'steam167'));
}
