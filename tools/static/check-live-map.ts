// check-live-map: generates mods/ts3fixes/src/CheckLiveMap.cs, the live-path assert behind B582Counters / B582Line
// (batch checks of docs/574-592). A batch Check used to test only its pure decision with constants, so a hook whose
// swap was missing, or whose reflection handle could not be used the way the hook uses it, still printed FIXED (the
// docs/587 §14.1 hypnotrickrelease case). For every switch this tool finds, from the source:
//   - the hook classes its Apply method patches (typeof(OurClass) in Patch calls) and how many unconditional swaps each
//     has (Patch at the top level of the Apply body), so EpLive (EpLiveCheck.cs) can confirm the swaps are in sPatched;
//   - the reflection handles each hook class uses (sX.GetValue / SetValue / Invoke on a static FieldInfo, PropertyInfo or
//     MethodInfo) and how: a primitive cast type, a static read (GetValue(null)), an Invoke argument count.
// The switch of a hook is the ShortcutOn("...") literal in the hook class, else in its Apply section.
// It also lists the "constant gap": a batch Check whose pure decision functions are not called by any hook code (the
// Check then proves a parallel copy, not the live path).
// Usage: node tools/static/check-live-map.ts            write CheckLiveMap.cs, print the gap list
//        node tools/static/check-live-map.ts --check    exit 1 when CheckLiveMap.cs is stale or a batch Check has the gap
//        node tools/static/check-live-map.ts --self-check
// EP and store checks (Ep*, Store*) build their EpLive calls by hand and are left out.
import { readFileSync, readdirSync, writeFileSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import assert from 'node:assert/strict';

const ROOT = resolve(import.meta.dirname, '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const OUT = join(SRC, 'CheckLiveMap.cs');
// Check files of batches 3-12 (docs/574-592): the scope of the constant-gap gate.
const BATCH_CHECKS = /^(Batch5\d\d[a-z]?Checks|Batch1\dChecks)\.cs$/;
const SKIP = /^(Ep|Store)|^CheckLiveMap\.cs$|^EpLiveCheck\.cs$/;
const PRIM = new Set(['int', 'bool', 'float', 'uint', 'long', 'ulong', 'string', 'double', 'short', 'ushort', 'byte']);

// Body text from the `{` at or after i to its matching `}` (strings and chars skipped).
export function block(s: string, i: number): [number, number] {
  const a = s.indexOf('{', i);
  let d = 0;
  for (let k = a; k < s.length; k++) {
    const ch = s[k];
    if (ch === '"' || ch === "'") { k++; while (k < s.length && s[k] !== ch) { if (s[k] === '\\') k++; k++; } continue; }
    if (ch === '/' && s[k + 1] === '/') { k = s.indexOf('\n', k); if (k < 0) break; continue; }
    if (ch === '{') d++;
    else if (ch === '}' && --d === 0) return [a, k + 1];
  }
  return [a, s.length];
}

type Handle = { name: string; spec: string };
type Hook = { cls: string; swaps: number; switches: Set<string>; handles: Handle[] };

export function scan(files: Map<string, string>) {
  const ours = new Set<string>();
  const handleKind = new Map<string, string>();
  for (const s of files.values()) {
    for (const m of s.replace(/\/\/.*$/gm, '').matchAll(/\bclass (\w+)/g)) ours.add(m[1]);  // not in comments ("household-class Sim")
    // only members of partial class Fixes (8-space indent): handles of other classes are not in scope in CheckLiveMap.cs
    for (const m of s.matchAll(/^ {8}(?:(?:private|internal|public) )?static (?:readonly )?(FieldInfo|MethodInfo|PropertyInfo)\s+(s\w+)\b/gm)) handleKind.set(m[2], m[1]);
  }
  const classBody = new Map<string, string>();
  for (const s of files.values()) for (const m of s.matchAll(/\bclass (\w+)[^{;]*\{/g)) {
    const [a, b] = block(s, m.index!); classBody.set(m[1], s.slice(a, b));
  }
  // docs/622 §3: SwOn(kSwX) asks the switch its slot names (static readonly int kSwX = SwSlot("name"))
  const slot = new Map<string, string>();
  for (const s of files.values()) for (const m of s.matchAll(/\b(kSw\w+) = SwSlot\("(\w+)"\)/g)) slot.set(m[1], m[2]);
  const names = (t: string) => new Set([...[...t.matchAll(/ShortcutOn\("(\w+)"\)/g)].map((x) => x[1]),
    ...[...t.matchAll(/\bSwOn\((kSw\w+)\)/g)].map((x) => slot.get(x[1])).filter((x): x is string => !!x)]);
  const hooks = new Map<string, Hook>();
  for (const [f, s] of files) {
    if (SKIP.test(f)) continue;
    const applies = [...s.matchAll(/static void (Apply\w+)\(\)\s*\{/g)];
    applies.forEach((m, n) => {
      const [a, b] = block(s, m.index!);
      const body = s.slice(a + 1, b - 1);
      const seg = s.slice(m.index!, n + 1 < applies.length ? applies[n + 1].index! : s.length);
      const segSw = names(seg);
      const lines = body.split('\n');
      let depth = 0, prev = '';
      for (const line of lines) {
        const t = line.trim();
        for (const p of t.matchAll(/\bPatch\(([^;]*)\);/g)) {
          const types = [...p[1].matchAll(/typeof\((\w+)\)/g)].map((x) => x[1]).filter((x) => ours.has(x));
          const top = depth === 0 && t.startsWith('Patch(') && !/\)\s*$/.test(prev) && !/^else\b/.test(prev);
          for (const cls of types) {
            const h = hooks.get(cls) ?? { cls, swaps: 0, switches: new Set<string>(), handles: [] };
            if (top) h.swaps++;
            const own = names(classBody.get(cls) ?? '');
            for (const w of own.size ? own : segSw) h.switches.add(w);
            hooks.set(cls, h);
          }
        }
        for (const ch of t.replace(/"(?:[^"\\]|\\.)*"/g, '')) { if (ch === '{') depth++; else if (ch === '}') depth--; }
        if (t) prev = t;
      }
    });
  }
  for (const h of hooks.values()) {
    const body = classBody.get(h.cls) ?? '';
    const seen = new Set<string>();
    const add = (name: string, spec: string) => { const k = name + '|' + spec; if (!seen.has(k)) { seen.add(k); h.handles.push({ name, spec }); } };
    for (const m of body.matchAll(/\b(s[A-Z]\w*)\.(GetValue|SetValue|Invoke)\(/g)) {
      const name = m[1], kind = handleKind.get(name);
      if (!kind) continue;
      const rest = body.slice(m.index! + m[0].length, m.index! + m[0].length + 200);
      if (kind === 'PropertyInfo') { add(name, m[2] === 'SetValue' ? '"set"' : '"get"'); continue; }
      if (kind === 'FieldInfo') {
        const before = body.slice(Math.max(0, m.index! - 12), m.index!);
        const cast = /\((\w+)\)\s*$/.exec(before);
        if (cast && PRIM.has(cast[1])) add(name, `typeof(${cast[1]})`);
        else add(name, 'null');
        if (m[2] === 'GetValue' && /^null\)/.test(rest)) add(name, '"static"');
        continue;
      }
      // MethodInfo.Invoke(target, args): args null -> 0, new object[] { a, b } -> 2
      const args = /^[^,()]*(?:\([^()]*\))?[^,()]*,\s*(null|new object\[\]\s*\{([^}]*)\})/.exec(rest);
      if (args) add(name, args[1] === 'null' ? '0' : String(topCommas(args[2] ?? '') + 1));
      else add(name, 'null');
    }
  }
  return hooks;
}

function topCommas(s: string): number {
  let d = 0, n = 0;
  for (const ch of s) { if ('([{<'.includes(ch)) d++; else if (')]}>'.includes(ch)) d--; else if (ch === ',' && d === 0) n++; }
  return n;
}

export function emit(hooks: Map<string, Hook>): string {
  const bySw = new Map<string, Hook[]>();
  for (const h of hooks.values()) for (const w of h.switches) { if (!bySw.has(w)) bySw.set(w, []); bySw.get(w)!.push(h); }
  const sws = [...bySw.keys()].sort();
  const o: string[] = [
    '// GENERATED by tools/static/check-live-map.ts from the ts3fixes source (build-fixes.ts runs it). Do not edit.',
    'using System;', '', 'namespace Ts3Fixes', '{', '    public static partial class Fixes', '    {',
    '        // Live-path problems of a switch\'s hooks (EpLive: swaps in sPatched, handles usable as the hook uses them); "" when none.',
    '        static string CkLive(string sw)', '        {', '            switch (sw)', '            {'];
  for (const w of sws) {
    const parts = bySw.get(w)!.sort((a, b) => a.cls.localeCompare(b.cls)).map((h) => {
      const tr = h.handles.map((x) => `"${x.name}", ${x.name}, ${x.spec}`).join(', ');
      return `EpLive(typeof(${h.cls}), ${h.swaps}${tr ? ', ' + tr : ''})`;
    });
    let e = parts[0];
    for (const p of parts.slice(1)) e = `EpJoin(${e}, ${p})`;
    o.push(`                case "${w}": return ${e};`);
  }
  o.push('                default: return "";', '            }', '        }', '    }', '}', '');
  return o.join('\n');
}

// Batch Checks whose pure decision functions no hook code calls.
export function gaps(files: Map<string, string>): string[] {
  const pure = new Map<string, string>();
  for (const [f, s] of files) if (!/Checks?\.cs$/.test(f)) for (const m of s.matchAll(/internal static [\w<>\[\], .]+? (\w+)(?:<\w+>)?\(/g)) pure.set(m[1], f);
  const live = new Set<string>();
  for (const [f, s] of files) {
    if (/Checks?\.cs$/.test(f) || f === 'CheckLiveMap.cs') continue;
    for (const name of pure.keys()) {
      const re = new RegExp(`\\b${name}\\s*(?:<[^>]*>)?\\(`, 'g');
      const n = [...s.matchAll(re)].length - (pure.get(name) === f ? 1 : 0);
      if (n > 0) live.add(name);
    }
  }
  const out: string[] = [];
  for (const [f, s] of files) {
    if (!BATCH_CHECKS.test(f)) continue;
    for (const m of s.matchAll(/public static object (\w+)\(string what\)/g)) {
      const [a, b] = block(s, m.index!);
      const body = s.slice(a, b);
      const called = [...new Set([...body.matchAll(/\b(\w+)\s*(?:<[^>]*>)?\(/g)].map((x) => x[1]).filter((x) => pure.has(x)))];
      // a Check with no pure decision reads only counters (a whole-method copy or an IL patch): CkLive is its live proof
      if (called.length && !called.some((x) => live.has(x))) out.push(`${m[1]} (${f}): calls only ${called.join(', ')}`);
    }
  }
  return out.sort();
}

function load(): Map<string, string> {
  const files = new Map<string, string>();
  for (const f of readdirSync(SRC).filter((n) => n.endsWith('.cs')).sort()) files.set(f, readFileSync(join(SRC, f), 'utf8'));
  return files;
}

function selfCheck() {
  const f = new Map<string, string>([['Batch1Logic.cs', `
        static readonly FieldInfo sAa = null;
        static readonly MethodInfo sAm = null;
    static void ApplyA() {
      if (x) return;
      Patch(typeof(T), "M", typeof(AHook), "H", "O");
      if (y)
          Patch(typeof(T), "N", typeof(AHook), "H2", "O2"); }
    internal static bool AaDecide(bool a) { return a; }
    internal static bool AaCopy(bool a) { return a; }
    public class AHook { public void H() { if (!ShortcutOn("aa")) return; int v = (int)sAa.GetValue(null); sAm.Invoke(this, new object[] { 1, f(2, 3) }); AaDecide(true); } }
  `], ['Batch1Checks.cs', `
    public static object AaCheck(string what) { return AaDecide(true); }
    public static object AbCheck(string what) { return AaCopy(true); }
  `]].map(([k, v]) => [k.replace('Batch1', 'Batch599'), v]) as [string, string][]);
  const h = scan(f).get('AHook')!;
  assert.equal(h.swaps, 1);
  assert.deepEqual([...h.switches], ['aa']);
  assert.deepEqual(h.handles, [{ name: 'sAa', spec: 'typeof(int)' }, { name: 'sAa', spec: '"static"' }, { name: 'sAm', spec: '2' }]);
  assert.match(emit(scan(f)), /case "aa": return EpLive\(typeof\(AHook\), 1, "sAa", sAa, typeof\(int\)/);
  assert.deepEqual(gaps(f), ['AbCheck (Batch599Checks.cs): calls only AaCopy']);
  console.log('check-live-map self-check ok');
}

if (process.argv.includes('--self-check')) selfCheck();
else {
  const files = load();
  files.delete('CheckLiveMap.cs');
  const text = emit(scan(files));
  const g = gaps(files);
  if (process.argv.includes('--check')) {
    const stale = !existsSync(OUT) || readFileSync(OUT, 'utf8') !== text;
    if (stale) console.log('check-live-map: CheckLiveMap.cs is stale (run without --check)');
    for (const x of g) console.log('GAP ' + x);
    process.exit(stale || g.length ? 1 : 0);
  }
  writeFileSync(OUT, text);
  for (const x of g) console.log('GAP ' + x);
  console.log(`check-live-map: ${scan(files).size} hooks, ${g.length} batch checks with the constant gap`);
}
