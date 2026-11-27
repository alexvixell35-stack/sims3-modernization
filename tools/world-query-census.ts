// World-wide query census over decompiled C# (docs/356, RX-2a of docs/340; RX-2b runs it over mod sources).
// For every call that asks for all objects, Sims, households, descriptions or lots of the world it prints one CSV row:
// who asks, for which type, what it does with the answer and how often it can run. Static and heuristic: the "ask" and
// "freq" columns come from the text rules below, not from a compiler.
//
//   node tools/world-query-census.ts [--out file.csv] [dir ...]   default dirs: the four EA script assemblies under
//                                                                  game-copy/decompiled (local only, never committed)
//   node tools/world-query-census.ts --self-check
//
// Works on Windows, Linux and macOS (Node >= 22.18, no dependencies).
import { readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

type Row = { family: string; assembly: string; file: string; line: number; cls: string; method: string; type: string; ask: string; freq: string; via: string };
type Src = { assembly: string; file: string; text: string; lines: string[] };

// family -> pattern. Group 1 (when present) is the type asked for.
const FAMILIES: [string, RegExp][] = [
  ['objects-all', /\bQueries\.GetObjects<([^()]+?)>\(\)/g],
  ['objects-all', /\bQueries\.GetObjects\(typeof\(([^()]+?)\)\)/g],
  ['objects-count', /\bQueries\.CountObjects<([^()]+?)>\(\)/g],
  ['objects-global', /\bQueries\.(?:Get|Count)GlobalObjects<([^()]+?)>\(\)/g],
  ['sims-all', /\bLotManager\.Actors\b/g],
  ['lots-all', /\bLotManager\.(?:AllLotsWithoutCommonExceptions|AllLots|Lots|GetAllCommunityLots\(\))(?![\w(])/g],
  ['households-all', /\bHousehold\.(?:sHouseholdList|GetHouseholdsLivingInWorld\(\))/g],
  ['descriptions-all', /\bSimDescription\.(?:GetAll\(|GetSimDescriptionsInWorld\(|GetHomelessSimDescriptionsFromUrnstones\()/g],
  ['description-by-id', /\bSimDescription\.Find\(/g],
];

const CONTROL = /^(if|for|foreach|while|switch|using|lock|else|try|catch|finally|do|return|fixed|checked|unchecked|case|default)\b/;
const ARROW = /^\t+(?:(?:public|private|protected|internal|static|override|virtual|sealed|new)\s+)+[\w<>\[\],.? ]+?\s+(\w+)(?:\([^)]*\))?\s*=>/;
const indent = (l: string) => l.length - l.replace(/^\t+/, '').length;
const esc = (x: string) => x.replace(/[^\w]/g, '\\$&');

// Enclosing class and member of line i (ILSpy layout: tabs, the brace on its own line).
function enclosing(lines: string[], i: number): { cls: string; method: string; start: number; end: number } {
  let method = ARROW.exec(lines[i])?.[1] ?? '', cls = '', start = method ? i : 0, depth = indent(lines[i]);
  for (let k = i - 1; k >= 0 && depth > 0; k--) {
    const d = indent(lines[k]), t = lines[k].trim();
    if (!t || d >= depth || (lines[k + 1] ?? '') !== '\t'.repeat(d) + '{') continue;   // not the header of a block around us
    depth = d;
    const c = /\b(?:class|struct|interface)\s+(\w+)/.exec(t);
    if (c) { cls = cls ? c[1] + '.' + cls : c[1]; continue; }
    if (cls || method || CONTROL.test(t) || /^(get|set|add|remove)$/.test(t)) continue;  // accessor: the property line is next
    const sig = t.replace(/^\[.*?\]\s*/, '');
    method = (sig.includes('(') ? /([\w.]+)\s*(?:<[^>()]*>)?\s*\(/.exec(sig)?.[1] : /(\w+)$/.exec(sig)?.[1]) ?? '?';
    start = k;
  }
  let end = start;
  if (start !== i || !ARROW.test(lines[i])) {
    const md = indent(lines[start]);
    end = start + 1;
    while (end < lines.length - 1 && lines[end] !== '\t'.repeat(md) + '}') end++;
  }
  return { cls: cls || '?', method: method || '?', start, end };
}

const WRITE = /\.(?:Set|Add|Remove|Destroy|Dispose|Reset|Fix|Push|Fade|Start|Stop|Update|Refresh|Enable|Disable|Make|Clear|Init|Register|Unregister|Force|Trigger|Spawn|Delete|Kill|Move|Place|Teleport|Repair|Break|Clean|Switch|Turn|Discover|Validate|Verify)\w*\(/;
const ZERO = String.raw`\s*(?:[><!=]=?\s*0\b|>=?\s*1\b)`;

// Body of the block whose header is line h (the lines between its braces).
function block(lines: string[], h: number, end: number): string {
  const d = indent(lines[h]), body: string[] = [];
  for (let k = h + 1; k <= end && lines[k] !== '\t'.repeat(d) + '}'; k++) body.push(lines[k]);
  return body.join('\n');
}

function loopAsk(body: string, v: string): string {
  if (/Distance|Closest|Nearest/.test(body)) return 'nearest';
  if (new RegExp(`\\b${v}\\.[\\w.]+\\s*(?:=[^=]|\\+=|-=|\\+\\+|--)`).test(body) || new RegExp(`\\b${v}(?:\\.\\w+)*${WRITE.source}`).test(body)
    || /InteractionQueue\.(?:Add|Push)|\.Destroy\(|DestroyObject\(/.test(body)) return 'iterate-write';
  return /\b(?:return|break)\b/.test(body) ? 'find-first' : 'iterate-read';
}

// What the caller does with the answer, from the statement and the code that follows it in the same member.
function askOf(lines: string[], i: number, call: string, family: string, end: number): string {
  const l = lines[i], at = l.indexOf(call), before = l.slice(0, at), after = l.slice(at + call.length);
  if (family === 'description-by-id') return 'lookup';
  if (family === 'objects-count' || /^\)*\.(?:Length|Count)\b/.test(after) || /CountGlobalObjects/.test(call)) {
    return new RegExp('^(?:\\)*\\.(?:Length|Count))?' + ZERO).test(after) ? 'exists' : 'count';
  }
  if (/^\)*\[\w+\]/.test(after)) return 'pick';
  if (/GetRandomObjectFromList\([^;]*$/.test(before)) return 'pick';
  const fe = /foreach\s*\(\s*[\w<>\[\],. ?]+\s+(\w+)\s+in\s+[^;]*$/.exec(before);
  if (fe) return loopAsk(block(lines, i, end), fe[1]);
  if (/\.(?:Contains|IndexOf)\(/.test(after)) return 'exists';
  if (/^\s*return\b/.test(l) || ARROW.test(l)) return 'wrapper';
  const first = /([\w.]+)\s*=(?!=)\s*[^;=]*$/.exec(before)?.[1]?.split('.').pop();
  if (!first) return /\(\s*$|,\s*$/.test(before.replace(/(?:Sims3\.\w+\.)$/, '')) ? 'wrapper' : 'discard';
  const names = [first];
  let exists = false, count = false, pick = false, wrapper = false;
  for (let k = i + 1; k <= end; k++) {
    const u = lines[k], any = names.map(esc).join('|');
    const alias = new RegExp(`(\\w+)\\s*=\\s*(?:new\\s+[\\w<>\\[\\], .]+\\(\\s*)?(?:\\([\\w<>\\[\\]., ]+\\)\\s*)?(?:${any})\\s*\\)?;`).exec(u);
    if (alias && !names.includes(alias[1])) { names.push(alias[1]); continue; }
    const f = new RegExp(`foreach\\s*\\(\\s*[\\w<>\\[\\],. ?]+\\s+(\\w+)\\s+in\\s+(?:${any})\\b`).exec(u);
    if (f) return loopAsk(block(lines, k, end), f[1]);
    if (new RegExp(`^\\s*(?:for|while)\\s*\\(.*\\b(?:${any})\\.(?:Length|Count)\\b`).test(u)) {
      const b = block(lines, k, end);
      return loopAsk(b, new RegExp(`(\\w+)\\s*=\\s*(?:${any})\\[`).exec(b)?.[1] ?? names[0] + '\\[\\w+\\]');
    }
    if (new RegExp(`\\b(?:${any})\\.(?:Length|Count)\\b${ZERO}|\\b(?:${any})\\s*[!=]=\\s*null`).test(u)) exists = true;
    else if (new RegExp(`\\b(?:${any})\\.(?:Length|Count)\\b`).test(u)) count = true;
    if (new RegExp(`GetRandomObjectFromList\\w*\\(\\s*(?:${any})\\b|\\b(?:${any})\\[`).test(u)) pick = true;
    if (new RegExp(`^\\s*return\\s+(?:${any})\\b|[(,]\\s*(?:${any})\\s*[,)]|\\.AddRange\\(\\s*(?:${any})\\s*\\)`).test(u)) wrapper = true;
  }
  return pick ? 'pick' : wrapper ? 'wrapper' : count ? 'count' : exists ? 'exists' : 'stored';
}

// How often the member can run, from its name and class and from who names it as an alarm callback in the same file.
function freqOf(cls: string, method: string, text: string): string {
  if (/Cheat|Debug|Commands\b|ReferenceScanner|ScriptError|Tuning\b/.test(cls) || /Cheat|Debug|^LogAll|^Dump/.test(method)) return 'cheat';
  if (/^(Simulate|Loop\w*)$/.test(method)) return 'tick-loop';
  if (/^(Test|InRabbitholeTest|TestAccept\w*|GetInteractionName|CallbackTest\w*)$/.test(method) || /Test$|^Test[A-Z]/.test(method)) return 'interaction-test';
  if (method !== '?' && new RegExp(`AddAlarm(?:Repeating|Day)\\w*\\([^;]*\\b${esc(method)}\\b`).test(text)) return 'alarm-repeating';
  if (method !== '?' && new RegExp(`AddAlarm\\w*\\([^;]*\\b${esc(method)}\\b`).test(text)) return 'alarm-once';
  if (/^(Run|RunBehavior|Run\w*Behavior|RunFromInventory|InRabbitHole|Perform\w*|Do\w*Loop\w*|OnPickFromPieMenu)$/.test(method)) return 'interaction-run';
  if (/AddInteractions|PopulatePieMenuPicker|MapTag|Thumbnail|String$|PropertiesForSale|Picker|Dialog|Tooltip/.test(method) || /Model$|Dialog|HUD|Hud/.test(cls)) return 'ui';
  if (/Load|Startup|StartUp|Fixup|FixUp|^Init|Parse|Shutdown|Dispose|Import|Export|PreSave|PostSave|^\.?c?ctor$|WorldChange|Travel|WorldInit|PreWorld|Validate|Cleanup|CleanUp|Nuke|Verify|Ensure|Purge|WorldBuilder|DeleteBad|PrepareToBecome/.test(method)
    || method === cls.split('.').pop()) return 'load';
  if (/^(get_|set_)?On[A-Z]\w*|Callback|Handler|Listener|Clicked|Changed$|CheckFunction$|Event$/.test(method)) return 'event';
  return 'other';
}
const HOT = ['tick-loop', 'interaction-test', 'alarm-repeating', 'interaction-run', 'alarm-once', 'event', 'ui', 'load', 'cheat'];

function scan(src: Src): Row[] {
  const { lines, text } = src, rows: Row[] = [];
  lines.forEach((l, i) => {
    if (/\bstatic\b.*\b(?:Actors|Lots|AllLots)\b\s*=>/.test(l)) return;             // the list's own declaration
    for (const [family, re] of FAMILIES) {
      for (const m of l.matchAll(re)) {
        const e = enclosing(lines, i);
        rows.push({ family, assembly: src.assembly, file: src.file, line: i + 1, cls: e.cls, method: e.method, type: m[1] ?? '',
          ask: askOf(lines, i, m[0], family, e.end), freq: freqOf(e.cls, e.method, text), via: '' });
      }
    }
  });
  return rows;
}

// Rows the name rules left open: take the hottest caller (one hop). A caller counts only when it names the class
// (`Class.Member(`) or sits in the same file and calls the member unqualified, so `x.Reset()` of another class is no match.
function traceCallers(rows: Row[], srcs: Src[]): void {
  const open = rows.filter(r => r.freq === 'other' && r.method !== '?');
  const best = new Map<string, [string, string]>();
  const keys = new Map(open.map(r => [r.file + '|' + r.cls + '|' + r.method, r]));
  for (const s of srcs) {
    for (const [key, r] of keys) {
      const own = s.file === r.file, last = r.cls.split('.').pop()!;
      if (!s.text.includes(own ? r.method + '(' : last + '.' + r.method + '(')) continue;
      const re = new RegExp(own ? `(?:^|[^.\\w]|\\b(?:this|${esc(last)})\\.)${esc(r.method)}\\(` : `\\b${esc(last)}\\.${esc(r.method)}\\(`);
      s.lines.forEach((l, k) => {
        if (!re.test(l) || (s.lines[k + 1] ?? '').trim() === '{') return;        // skip declarations
        const c = enclosing(s.lines, k);
        if (c.method === r.method) return;
        const f = freqOf(c.cls, c.method, s.text), old = best.get(key);
        if (f !== 'other' && (!old || HOT.indexOf(f) < HOT.indexOf(old[0]))) best.set(key, [f, c.cls + '.' + c.method]);
      });
    }
  }
  // Still open: a member name declared once in all sources cannot be confused, so any `Name(` call is a caller.
  const left = [...keys].filter(([key]) => !best.has(key));
  const decls = new Map<string, number>();
  const callers = new Map<string, [string, string]>();
  for (const s of srcs) {
    for (const n of new Set(left.map(([, r]) => r.method))) {
      if (!s.text.includes(n + '(')) continue;
      const re = new RegExp(`\\b${esc(n)}\\(`);
      s.lines.forEach((l, k) => {
        if (!re.test(l)) return;
        if ((s.lines[k + 1] ?? '').trim() === '{') { decls.set(n, (decls.get(n) ?? 0) + 1); return; }
        const c = enclosing(s.lines, k);
        const f = freqOf(c.cls, c.method, s.text), old = callers.get(n);
        if (c.method !== n && f !== 'other' && (!old || HOT.indexOf(f) < HOT.indexOf(old[0]))) callers.set(n, [f, c.cls + '.' + c.method]);
      });
    }
  }
  for (const [key, r] of left) if (decls.get(r.method) === 1 && callers.has(r.method)) best.set(key, callers.get(r.method)!);
  for (const r of open) {
    const b = best.get(r.file + '|' + r.cls + '|' + r.method);
    if (b) { r.freq = b[0]; r.via = b[1]; }
  }
  for (const r of rows) if (r.freq === 'other') r.freq = 'helper';   // caller not traced: properties, delegates, instance calls from other files
}

function walk(dir: string, out: string[] = []): string[] {
  for (const n of readdirSync(dir)) {
    const p = join(dir, n);
    if (statSync(p).isDirectory()) walk(p, out); else if (n.endsWith('.cs')) out.push(p);
  }
  return out;
}

function selfCheck(): void {
  const text = `namespace N
{
	public class Fridge : GameObject
	{
		public sealed class Eat : Interaction
		{
			public override bool Test(Sim a, Fridge target)
			{
				return Queries.GetObjects<Stove>().Length > 0;
			}

			public override bool Run()
			{
				foreach (Urnstone item in Queries.GetObjects<Urnstone>())
				{
					item.FadeOut();
				}
				return true;
			}
		}

		public SimDescription Owner => SimDescription.Find(mId);

		public void OnTimer()
		{
			Sim[] array = Sims3.Gameplay.Queries.GetObjects<Sim>();
			Sim[] array2 = array;
			foreach (Sim sim in array2)
			{
				if (sim.IsHuman)
				{
					return;
				}
			}
		}

		public void OnStartup()
		{
			AddAlarmRepeating(1f, TimeUnit.Hours, OnTimer, "x", AlarmType.AlwaysPersisted);
			foreach (Sim actor in LotManager.Actors)
			{
				Log(actor);
			}
			uint n = Queries.CountObjects<Mailbox>();
			switch (n)
			{
			case 1u:
			{
				Lamp[] array = Queries.GetObjects<Lamp>();
				for (int i = 0; i < array.Length; i++)
				{
					Lamp lamp = array[i];
					lamp.SwitchOff();
				}
				break;
			}
			}
		}

		private static Stove[] Stoves()
		{
			return Queries.GetObjects<Stove>();
		}

		public static void SomeHelper()
		{
			if (Queries.CountObjects<Sink>() == 0)
			{
				Stove[] array = Stoves();
			}
		}
	}

	public class Other
	{
		public void Simulate()
		{
			Fridge.SomeHelper();
		}
	}
}`;
  const src: Src = { assembly: 'A', file: 'f.cs', text, lines: text.split('\n') };
  const rows = scan(src);
  traceCallers(rows, [src]);
  const got = rows.map(r => [r.family, r.cls, r.method, r.type, r.ask, r.freq, r.via].join('|'));
  const want = [
    'objects-all|Fridge.Eat|Test|Stove|exists|interaction-test|',
    'objects-all|Fridge.Eat|Run|Urnstone|iterate-write|interaction-run|',
    'description-by-id|Fridge|Owner||lookup|helper|',
    'objects-all|Fridge|OnTimer|Sim|find-first|alarm-repeating|',
    'sims-all|Fridge|OnStartup||iterate-read|load|',
    'objects-count|Fridge|OnStartup|Mailbox|count|load|',
    'objects-all|Fridge|OnStartup|Lamp|iterate-write|load|',
    'objects-all|Fridge|Stoves|Stove|wrapper|helper|',
    'objects-count|Fridge|SomeHelper|Sink|exists|tick-loop|Other.Simulate',
  ];
  let fail = 0;
  want.forEach((w, i) => { if (got[i] !== w) { fail++; console.log(`FAIL row ${i}: got ${got[i]} want ${w}`); } });
  if (got.length !== want.length) { fail++; console.log(`FAIL rows: got ${got.length} want ${want.length}`); }
  console.log(fail ? `self-check: ${fail} FAIL` : `self-check: ${want.length} PASS`);
  process.exit(fail ? 1 : 0);
}

const args = process.argv.slice(2);
if (args.includes('--self-check')) selfCheck();
const oi = args.indexOf('--out');
const out = oi >= 0 ? args.splice(oi, 2)[1] : '';
const root = join('game-copy', 'decompiled');
const dirs = args.length ? args : ['Sims3GameplaySystems', 'Sims3GameplayObjects', 'Sims3StoreObjects', 'UI'].map(a => join(root, a));
const srcs: Src[] = [];
for (const d of dirs) {
  const assembly = d.split(/[\\/]/).filter(Boolean).pop()!;
  for (const f of walk(d)) {
    const text = readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
    srcs.push({ assembly, file: relative(d, f).split(sep).join('/'), text, lines: text.split('\n') });
  }
}
const rows = srcs.flatMap(scan);
traceCallers(rows, srcs);
const cell = (x: string | number) => /[",]/.test(String(x)) ? `"${String(x).replace(/"/g, '""')}"` : String(x);
const csv = ['family,assembly,file,line,class,method,type,ask,freq,via',
  ...rows.map(r => [r.family, r.assembly, r.file, r.line, r.cls, r.method, r.type, r.ask, r.freq, r.via].map(cell).join(','))].join('\n') + '\n';
if (out) writeFileSync(out, csv);
const tally = (k: keyof Row) => Object.entries(rows.reduce((a: Record<string, number>, r) => (a[String(r[k])] = (a[String(r[k])] ?? 0) + 1, a), {}))
  .sort((a, b) => b[1] - a[1]).map(x => x.join(' ')).join(', ');
console.log(`${rows.length} sites${out ? ' -> ' + out : ''}\nfamily: ${tally('family')}\nask: ${tally('ask')}\nfreq: ${tally('freq')}`);
