// A measuring hook must be the outermost swap of its method. Fixes.Patch stacks swaps of one method: the hook patched
// later runs first and reaches the earlier one only through its own stub. A fix hook that answers without calling its stub
// (initstacktrace's OnAddedToQueue re-does EA's body) then cuts off every hook patched before it. That is what left the
// actdelay probe with no records in s218 / s219 (docs/684 §7): its OnAddedToQueue swap went in before initstacktrace's.
//   node tools/probe-swap-order.ts               gate, no game files: for every method a file in OUTERMOST swaps, no other
//                                                file's swap of that method is applied later (order = the Apply...() calls
//                                                of Fixes.Apply, then Fixes.ApplyTuned, then the line inside the function)
//   node tools/probe-swap-order.ts --self-check  gate plus unit checks on small fixtures
import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import assert from 'node:assert/strict';

const ROOT = resolve(import.meta.dirname, '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
// files whose swaps only measure and must see every call
const OUTERMOST = ['ActDelay.cs'];

export type Swap = { file: string; fn: string; line: number; key: string; hook: string };

// the swaps of one source file, each with the function that makes it
export function fileSwaps(text: string, file: string): Swap[] {
  const out: Swap[] = [];
  let fn = '';
  const lines = text.split('\n');
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    const f = /^\s*(?:public |internal |private )?static (?:void|bool) (\w+)\(/.exec(l);
    if (f) fn = f[1];
    if (/^\s*\/\//.test(l)) continue;
    const m = /\bPatch\(typeof\(([\w.]+)\), "(\w+)",(.*)$/.exec(l);
    if (!m) continue;
    const h = /typeof\((\w+)\), "\w+", (?:"\w+"|null)\)/.exec(m[3]);
    out.push({ file, fn, line: i + 1, key: `${m[1].split('.').pop()}.${m[2]}`, hook: h ? h[1] : '?' });
  }
  return out;
}

// the order the Apply...() functions run in: calls inside Fixes.Apply, then inside Fixes.ApplyTuned
export function applyOrder(fixes: string): string[] {
  const order: string[] = [];
  for (const name of ['Apply', 'ApplyTuned']) {
    const start = fixes.search(new RegExp(`static void ${name}\\(`));
    if (start < 0) continue;
    const rest = fixes.slice(start);
    const end = rest.slice(1).search(/\n {8}(?:\/\/ [^\n]*\n {8})*(?:public |internal |private )?static /);
    const body = end < 0 ? rest : rest.slice(0, end + 1);
    order.push(name);   // swaps made directly in the function body
    for (const m of body.matchAll(/^\s*(?:try \{ )?(Apply\w+)\(/gm)) if (!order.includes(m[1])) order.push(m[1]);
  }
  return order;
}

export function gate(swaps: Swap[], order: string[], outermost: string[]): string[] {
  const bad: string[] = [];
  const rank = (s: Swap): number => { const i = order.indexOf(s.fn); return (i < 0 ? order.length : i) * 100000 + s.line; };
  for (const s of swaps) {
    if (!outermost.includes(s.file)) continue;
    if (!order.includes(s.fn)) bad.push(`${s.file}: ${s.fn} is not called from Fixes.Apply / ApplyTuned (order unknown)`);
    for (const o of swaps) {
      if (o.file === s.file || o.key !== s.key) continue;
      if (rank(o) > rank(s)) bad.push(`${s.key}: ${o.hook} (${o.file}, ${o.fn}) is patched after ${s.hook} (${s.file}, ${s.fn}), so it runs first and can cut the probe off: apply ${s.fn} later`);
    }
  }
  return bad;
}

function repoSwaps(): Swap[] {
  return readdirSync(SRC).filter((f) => f.endsWith('.cs')).flatMap((f) => fileSwaps(readFileSync(join(SRC, f), 'utf8'), f));
}

function runGate(): number {
  const swaps = repoSwaps();
  const bad = gate(swaps, applyOrder(readFileSync(join(SRC, 'Fixes.cs'), 'utf8')), OUTERMOST);
  for (const b of bad) console.log(`FAIL ${b}`);
  const mine = swaps.filter((s) => OUTERMOST.includes(s.file));
  if (!bad.length) console.log(`probe-swap-order: ${mine.length} probe swaps, each the outermost of its method (${swaps.length} swaps read)`);
  return bad.length || !mine.length ? 1 : 0;
}

function selfCheck(): void {
  const fixes = `        public static void Apply()
        {
            Patch(typeof(GoHome), "Run", typeof(GoHomeHook), "RunHook", "RunOrig");
            ApplyProbe();   // early
            ApplyFix();
        }

        // tuned
        internal static void ApplyTuned(string when)
        {
            ApplyLate();
        }

        static void TunedLog() { ApplyNever(); }`;
  assert.deepEqual(applyOrder(fixes), ['Apply', 'ApplyProbe', 'ApplyFix', 'ApplyTuned', 'ApplyLate']);
  const probe = fileSwaps(`        static void ApplyProbe()
        {
            // Patch(typeof(Commented), "Out", typeof(XHook), "H", "O");
            Patch(typeof(InteractionInstance), "OnAddedToQueue", Type.EmptyTypes, typeof(AdInstHook), "AddedHook", "AddedOrig");
            Patch(typeof(Sims3.Gameplay.Core.GlobalFunctions), "Find", sArgs, typeof(AdFglHook), "FglHook", "FglOrig");
        }`, 'Probe.cs');
  assert.deepEqual(probe.map((s) => `${s.fn}/${s.key}/${s.hook}`), ['ApplyProbe/InteractionInstance.OnAddedToQueue/AdInstHook', 'ApplyProbe/GlobalFunctions.Find/AdFglHook']);
  const fix = fileSwaps(`        static void ApplyFix()
        {
            ok = Patch(typeof(InteractionInstance), "OnAddedToQueue", Type.EmptyTypes, typeof(IstHook), "AddedHook", "AddedOrig");
        }
        static void ApplyLate() { }`, 'Fix.cs');
  // the s218 shape: the fix is patched after the probe
  assert.match(gate([...probe, ...fix], applyOrder(fixes), ['Probe.cs'])[0], /IstHook \(Fix\.cs, ApplyFix\) is patched after AdInstHook/);
  // the probe applied last: fine
  const fixed = fixes.replace('            ApplyProbe();   // early\n', '').replace('ApplyFix();', 'ApplyFix();\n            ApplyProbe();');
  assert.deepEqual(applyOrder(fixed), ['Apply', 'ApplyFix', 'ApplyProbe', 'ApplyTuned', 'ApplyLate']);
  assert.equal(gate([...probe, ...fix], applyOrder(fixed), ['Probe.cs']).length, 0);
  // a tuned patch of the same method still comes later
  const late = fix.map((s) => ({ ...s, fn: 'ApplyLate' }));
  assert.equal(gate([...probe, ...late], applyOrder(fixed), ['Probe.cs']).length, 1);
  // a probe function nobody calls
  assert.match(gate(probe, ['Apply'], ['Probe.cs'])[0], /order unknown/);
  assert.equal(runGate(), 0, 'repo gate');
  console.log('probe-swap-order self-check ok');
}

if (process.argv.includes('--self-check')) selfCheck();
else process.exit(runGate());
