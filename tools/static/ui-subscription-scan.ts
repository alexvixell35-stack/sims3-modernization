// UI subscription leak scan (docs/557). For every C# file of a local decompile, lists handlers added with `+=` to an
// event on a long-lived receiver (a model behind Responder.Instance, a field holding a model, a singleton `.Instance`,
// or a static event on a type) that the same file never removes with `-=` (same event name, same handler name).
// A UI panel that subscribes a model and is disposed without unsubscribing stays reachable from the model and keeps
// receiving its events: one more copy per panel open (the hudunsub family, docs/545).
//
// Usage: node tools/static/ui-subscription-scan.ts <decompiled dir> [--csv out.csv]
//        node tools/static/ui-subscription-scan.ts --self-check
// Reads only local files; output is method names and counts, no EA code.
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative } from 'node:path';

export interface Sub { file: string; line: number; receiver: string; event: string; handler: string; method: string }

// Receivers that outlive a panel. `this`-local windows (mButton.Click) die with the panel and are not reported.
const LONG_LIVED = /(^|\.)(\w*Model|Responder\.Instance|Instance|UIManager|CameraController|Simulator|World|EventTracker|GameStates|Audio|LoadSaveManager|Sims3\.SimIFace\.\w+)$/;
const STATIC_TYPE = /^[A-Z]\w*(\.[A-Z]\w*)*$/;

const ADD = /([A-Za-z_][\w.]*(?:\(\))?(?:\.[A-Za-z_]\w*)*)\.([A-Za-z_]\w*)\s*\+=\s*(?:new\s+[\w.<>]+\(\s*)?([A-Za-z_][\w.]*)\s*\)?\s*;/g;
const REMOVE = /([A-Za-z_][\w.]*(?:\(\))?(?:\.[A-Za-z_]\w*)*)\.([A-Za-z_]\w*)\s*-=\s*(?:new\s+[\w.<>]+\(\s*)?([A-Za-z_][\w.]*)\s*\)?\s*;/g;
const METHOD = /^\s*(?:(?:public|private|protected|internal|static|override|virtual|sealed|new|unsafe|extern)\s+)+[\w.<>\[\], ]+?\s+(\w+)\s*\(/;

function lastName(h: string): string { const p = h.split('.'); return p[p.length - 1]; }

export function longLived(receiver: string, fields: Set<string>): boolean {
  if (receiver === 'this' || receiver === 'base') return false;
  if (LONG_LIVED.test(receiver)) return true;
  // A capitalized dotted name that is not a field of the class is a static event on a type (UIManager.X, Foo.Bar).
  const head = receiver.split('.')[0];
  return STATIC_TYPE.test(receiver) && !fields.has(head) && !/^m[A-Z]/.test(head);
}

export function scanText(file: string, text: string): { adds: Sub[]; unmatched: Sub[] } {
  const lines = text.split('\n');
  const fields = new Set<string>();
  for (const m of text.matchAll(/^\s*(?:(?:public|private|protected|internal|static|readonly)\s+)+[\w.<>\[\], ]+\s+(\w+)\s*(?:=[^;]*)?;/gm)) fields.add(m[1]);
  const adds: Sub[] = [];
  const removes = new Set<string>();
  let method = '';
  lines.forEach((ln, i) => {
    const mm = METHOD.exec(ln);
    if (mm) method = mm[1];
    for (const m of ln.matchAll(ADD)) {
      if (/^\(|=>|delegate/.test(m[3])) continue;
      adds.push({ file, line: i + 1, receiver: m[1], event: m[2], handler: lastName(m[3]), method });
    }
    for (const m of ln.matchAll(REMOVE)) removes.add(m[2] + ' ' + lastName(m[3]));
  });
  const unmatched = adds.filter(a => longLived(a.receiver, fields) && !removes.has(a.event + ' ' + a.handler));
  return { adds, unmatched };
}

function walk(dir: string, out: string[]): string[] {
  for (const n of readdirSync(dir)) {
    const p = join(dir, n);
    if (statSync(p).isDirectory()) walk(p, out); else if (n.endsWith('.cs')) out.push(p);
  }
  return out;
}

function selfCheck(): void {
  const src = [
    'public class Panel {',
    '\tprivate IHudModel mHudModel;',
    '\tprivate Button mButton;',
    '\tpublic void Init() {',
    '\t\tmHudModel.SimChanged += OnSimChanged;',
    '\t\tmHudModel.MoneyChanged += new MoneyChangedEvent(OnMoney);',
    '\t\tResponder.Instance.CASModel.OnSimUpdated += OnSimUpdated;',
    '\t\tUIManager.OnSomething += OnSome;',
    '\t\tmButton.Click += OnClick;',
    '\t\tmHudModel.Fired += (s, e) => Foo();',
    '\t}',
    '\tpublic void Dispose() {',
    '\t\tmHudModel.SimChanged -= OnSimChanged;',
    '\t}',
    '}',
  ].join('\n');
  const r = scanText('x.cs', src);
  const got = r.unmatched.map(u => `${u.event}:${u.handler}:${u.method}`).sort().join(',');
  const want = 'MoneyChanged:OnMoney:Init,OnSimUpdated:OnSimUpdated:Init,OnSomething:OnSome:Init';
  if (got !== want) { console.error('self-check FAIL\n got  ' + got + '\n want ' + want); process.exit(1); }
  if (!longLived('Responder.Instance.HudModel', new Set()) || longLived('mButton', new Set(['mButton']))) { console.error('self-check FAIL longLived'); process.exit(1); }
  console.log('self-check PASS');
}

function main(): void {
  const args = process.argv.slice(2);
  if (args[0] === '--self-check') return selfCheck();
  if (!args[0]) { console.error('usage: node tools/static/ui-subscription-scan.ts <decompiled dir> [--csv out.csv] | --self-check'); process.exit(2); }
  const root = args[0];
  const csvAt = args.indexOf('--csv');
  const rows: Sub[] = [];
  let total = 0;
  for (const f of walk(root, [])) {
    const r = scanText(relative(root, f), readFileSync(f, 'utf8'));
    total += r.adds.length;
    rows.push(...r.unmatched);
  }
  const byFile = new Map<string, number>();
  for (const r of rows) byFile.set(r.file, (byFile.get(r.file) ?? 0) + 1);
  console.log(`${total} subscriptions, ${rows.length} on long-lived receivers with no matching -= in the same file, ${byFile.size} files`);
  for (const r of rows) console.log(`${r.file}:${r.line}\t${r.method}\t${r.receiver}.${r.event} += ${r.handler}`);
  if (csvAt >= 0) {
    const q = (s: string | number) => '"' + String(s).replace(/"/g, '""') + '"';
    writeFileSync(args[csvAt + 1], 'file,line,method,receiver,event,handler\n' +
      rows.map(r => [r.file, r.line, r.method, r.receiver, r.event, r.handler].map(q).join(',')).join('\n') + '\n');
  }
}

main();
