// Send a ts3native crash report (docs/135). ts3native [CrashReport] writes scrubbed text reports to
// <Documents>\Electronic Arts\The Sims 3\ts3native-crashes\. This lists the new ones, shows exactly what would be sent,
// and only after you say yes either uses your own logged-in GitHub CLI (`gh`: find an issue with the same signature and
// comment there, else open a new one) or opens a prefilled GitHub "new issue" page you submit yourself.
// Nothing is sent without your yes, and no token is stored anywhere.
//   node tools/report-crash.ts                 go through the new reports one by one
//   node tools/report-crash.ts --list          list them, send nothing
//   node tools/report-crash.ts --browser       never use gh, always the prefilled page
//   node tools/report-crash.ts --dir <folder> --repo <owner/name>
//   node tools/report-crash.ts --self-check    offline checks (CI)
// Error 12 report (docs/PLAYERS.md, [MemDiag] AllocSites=1): the memory lines of ts3native.log, scrubbed, package names hashed
//   node tools/report-crash.ts --memory [--log <Game\Bin\ts3native.log>] [--names]   --names keeps package names (opt-in)
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { homedir, hostname, tmpdir, userInfo } from 'node:os';
import { join, resolve } from 'node:path';
import { createInterface } from 'node:readline/promises';
import { parseArgs } from 'node:util';

const REPO = 'rodneypantonial/sims3-modernization';
const BODY_MAX = 60000;  // GitHub allows 65536 characters per issue or comment
const URL_MAX = 7500;    // browsers and GitHub cut long URLs (~8 KB)
const SIG_RE = /crashsig-[0-9a-f]{8}/;
const MEM_MARK = 'ts3native memory report';  // .github/workflows/crash-issues.yml labels issues carrying it
// lines tools/memory-report.ts reads (run header, memory samples, alloc sites, world loads), plus the log's last lines
const MEM_KEEP = /^(?:ts3native \S+ \d{4}-|diag:|allocsites:|typegrowth:|gcsize:|ts3fixes: (?:world:|script assemblies)|save: memory)/;
const MEM_TAIL = 20;

type Report = { file: string; text: string; fields: Record<string, string> };

// Documents folder as Windows has it (OneDrive may move it), else ~/Documents
function documents(): string {
  if (process.platform === 'win32') {
    try {
      const out = execFileSync('reg', ['query', 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders', '/v', 'Personal'],
        { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'], timeout: 10000 });
      const m = out.match(/Personal\s+REG_(?:EXPAND_)?SZ\s+(.+)/);
      if (m) return m[1].trim().replace(/%([^%]+)%/g, (all, v) => process.env[v] ?? all);
    } catch { /* fall through */ }
  }
  return join(homedir(), 'Documents');
}

function parse(text: string): Record<string, string> {
  const fields: Record<string, string> = {};
  for (const m of text.split('--- ')[0].matchAll(/^([a-z0-9 ]+): (.*?)\r?$/gm)) fields[m[1]] ??= m[2];
  return fields;
}

// Second guard on top of the mod's scrubber: this PC's user name, machine name and home folder, and any \Users\<x>.
function scrub(text: string, names = { user: userInfo().username, host: hostname(), home: homedir() }): string {
  const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  let t = text;
  const pairs: [string, string][] = [[names.home, '<home>'], [names.user, '<user>'], [names.host, '<machine>']];
  for (const [v, to] of pairs.sort((a, b) => b[0].length - a[0].length))  // longest first: a host 'box-zed' before a user 'zed'
    if (v && v.length >= 3) t = t.replace(new RegExp(esc(v), 'gi'), to);
  return t.replace(/([\\/])Users\1[^\\/\r\n"' ]+/gi, (_m, s) => `${s}Users${s}<user>`);
}

// Error 12 report text: kept lines, scrubbed as above, plus emails and \\machine\ shares; package names become pkg-<8 hex>
// (same name = same id across reports) unless the player opts in with keepNames
function memoryText(log: string, keepNames = false, names?: Parameters<typeof scrub>[1]): string {
  const lines = log.split(/\r?\n/);
  const kept = lines.filter((l, i) => MEM_KEEP.test(l) || (i >= lines.length - MEM_TAIL && l.trim()));
  let t = scrub(kept.join('\n'), names)
    .replace(/[\w.+-]+@[\w-]+(?:\.[\w-]+)+/g, '<email>')
    .replace(/\\\\[^\\\s]+\\/g, '\\\\<machine>\\');
  if (!keepNames) {
    const id = (p: string) => 'pkg-' + createHash('sha256').update(p.trim().toLowerCase()).digest('hex').slice(0, 8);
    // list items ("names): a b.package, c.package") first so names with spaces go whole, then any other *.package token
    t = t.replace(/(?<=^|: |, )[^,:\r\n]*?\.package\b/gim, id).replace(/[^\s,:;"'=]+\.package\b/gi, id);
  }
  return t + '\n';
}

const memVersion = (log: string) => /^ts3native (\S+) \d{4}-/m.exec(log)?.[1] ?? '?';
const memTitle = (ver: string) => `[memory] Error 12 report (ts3native ${ver})`;

// the start (run header, session lines) and the end (the samples before Error 12) matter most: cut the middle
function memBody(text: string, ver: string): string {
  const head = `${MEM_MARK}\n\n### ts3native version\n\n${ver}\n\n### Memory report\n\n`;
  let rep = text;
  const room = BODY_MAX - head.length - 300;
  if (rep.length > room) {
    const first = rep.split('\n').slice(0, 80).join('\n');
    rep = `${first}\n[cut here: the full report file is on the reporter's PC]\n${rep.slice(rep.length - (room - first.length))}`;
  }
  return `${head}\`\`\`text\n${rep}\`\`\`\n\n_Sent with tools/report-crash.ts --memory._\n`;
}

function memUrl(text: string, ver: string, repo = REPO): string {
  const base = `https://github.com/${repo}/issues/new?` + new URLSearchParams({ template: 'memory.yml', title: memTitle(ver), version: ver }).toString();
  const lines = text.split('\n');
  for (let n = lines.length; n > 0; n = Math.floor(n * 0.8)) {  // keep the end: the samples right before Error 12
    const rep = (n < lines.length ? '[cut: please attach the full report file]\n' : '') + lines.slice(lines.length - n).join('\n');
    const url = `${base}&${new URLSearchParams({ report: rep })}`;
    if (url.length <= URL_MAX) return url;
  }
  return base;
}

function title(r: Report): string {
  return r.fields.title ?? `[crash] ${r.fields.signature ?? 'unknown'} (ts3native ${r.fields.ts3native ?? '?'})`;
}

function body(r: Report): string {
  const head = [
    `### Signature id\n\n${r.fields['signature id'] ?? '?'}`,
    `### Signature\n\n\`${r.fields.signature ?? '?'}\``,
    `### ts3native version\n\n${r.fields.ts3native ?? '?'}`,
    `### Crash report\n\n`,
  ].join('\n\n');
  let rep = r.text.replace(/\r\n/g, '\n');
  const room = BODY_MAX - head.length - 200;
  if (rep.length > room) rep = rep.slice(0, room) + '\n[cut here: the full file is on the reporter\'s PC]\n';
  return `${head}\`\`\`text\n${rep}\`\`\`\n\n_Sent with tools/report-crash.ts._\n`;
}

// Prefilled issue-form page: the crash.yml field ids; the report is cut to fit the URL (the player can attach the file)
function issueUrl(r: Report, repo = REPO): string {
  const base = `https://github.com/${repo}/issues/new?` + new URLSearchParams({
    template: 'crash.yml', title: title(r), signature: r.fields['signature id'] ?? '', version: r.fields.ts3native ?? '',
  }).toString();
  const lines = r.text.replace(/\r\n/g, '\n').split('\n');
  for (let n = lines.length; n > 0; n = Math.floor(n * 0.8)) {
    const rep = lines.slice(0, n).join('\n') + (n < lines.length ? '\n[cut: please attach the full report file]' : '');
    const url = `${base}&${new URLSearchParams({ report: rep })}`;
    if (url.length <= URL_MAX) return url;
  }
  return base;
}

function gh(args: string[]): string {
  return execFileSync('gh', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 60000 }).trim();
}

function ghReady(): boolean {
  try { gh(['auth', 'status']); return true; } catch { return false; }
}

function openUrl(url: string) {
  const [cmd, args] = process.platform === 'win32' ? ['rundll32', ['url.dll,FileProtocolHandler', url]]
    : process.platform === 'darwin' ? ['open', [url]] : ['xdg-open', [url]];
  try { execFileSync(cmd, args, { stdio: 'ignore', timeout: 15000 }); } catch { /* the URL is printed anyway */ }
}

function sendWithGh(r: Report, repo: string): string {
  const id = r.fields['signature id'];
  const tmp = mkdtempSync(join(tmpdir(), 'ts3crash-'));
  const file = join(tmp, 'body.md');
  writeFileSync(file, body(r));
  try {
    const found = id && SIG_RE.test(id)
      ? JSON.parse(gh(['issue', 'list', '--repo', repo, '--state', 'all', '--search', `${id} in:body`, '--json', 'number,url', '--limit', '1']))
      : [];
    if (found.length) {
      console.log(`Same crash already reported: ${found[0].url}. Adding your report as a comment.`);
      return gh(['issue', 'comment', String(found[0].number), '--repo', repo, '--body-file', file]);
    }
    return gh(['issue', 'create', '--repo', repo, '--title', title(r), '--body-file', file]);
  } finally {
    rmSync(tmp, { recursive: true, force: true });
  }
}

async function memoryMain(v: { log?: string; names?: boolean; browser?: boolean; repo: string }) {
  const steam = 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\The Sims 3\\Game\\Bin\\ts3native.log';
  const log = v.log ?? [resolve('ts3native.log'), steam].find(f => existsSync(f));
  if (!log || !existsSync(log)) {
    console.log('No ts3native.log found. Pass it: --log "<your game folder>\\Game\\Bin\\ts3native.log"');
    return;
  }
  const raw = readFileSync(log, 'utf8');
  if (!/^allocsites: /m.test(raw)) console.log('Note: this log has no allocsites: lines. Was [MemDiag] AllocSites=1 set before you played? The report still has the memory samples.');
  const ver = memVersion(raw), text = memoryText(raw, v.names);
  const out = resolve('ts3native-memory-report.txt');
  writeFileSync(out, text);
  const useGh = !v.browser && ghReady();
  console.log('\n================ this is exactly what would be sent ================');
  console.log(`Where: ${useGh ? `a new GitHub issue in ${v.repo}, posted as your own gh account` : 'a GitHub page in your browser, prefilled; you press Submit'}`);
  console.log(`Title: ${memTitle(ver)}\n`);
  console.log(useGh ? memBody(text, ver) : decodeURIComponent(new URL(memUrl(text, ver, v.repo)).searchParams.get('report') ?? ''));
  console.log('================ end of what would be sent ================');
  console.log(`Package names are ${v.names ? 'KEPT (--names)' : 'replaced by pkg-<id> (add --names to keep them; it helps us tie memory to mods)'}. Scrubbed copy: ${out}`);
  const rl = createInterface({ input: process.stdin, output: process.stdout });
  try {
    if ((await rl.question('Send it? y = yes, anything else = no: ')).trim().toLowerCase() !== 'y') return;
  } finally {
    rl.close();
  }
  if (useGh) {
    const tmp = mkdtempSync(join(tmpdir(), 'ts3mem-'));
    try {
      writeFileSync(join(tmp, 'body.md'), memBody(text, ver));
      console.log(`Sent: ${gh(['issue', 'create', '--repo', v.repo, '--title', memTitle(ver), '--body-file', join(tmp, 'body.md')])}`);
    } finally {
      rmSync(tmp, { recursive: true, force: true });
    }
  } else {
    const url = memUrl(text, ver, v.repo);
    console.log(`Opening the prefilled page. Check it, press "Submit new issue", and attach ${out} if the text was cut:\n${url}`);
    openUrl(url);
  }
}

function selfCheck() {
  const sample = [
    'ts3native crash report 1', 'kind: exception', 'time: 2026-10-21T01:02:03Z',
    'title: [crash] TS3W+0x23456 (ts3native 0.36.7)', 'signature id: crashsig-0a1b2c3d',
    'signature: TS3W+0x23456 TS3W+0x1000 d3d9+0x42 TS3W+0x2000', 'ts3native: 0.36.7',
    'fault: TS3W+0x23456', 'stack (return addresses found on the stack, top first):', '  TS3W+0x1000',
    'exe sha256: abc', '--- package files (names only) ---', ...Array.from({ length: 900 }, (_, i) => `  cc-${i}.package`),
    'packages: 900', '--- ts3native.log (last 50 lines) ---', 'host C:\\Users\\<user>\\Steam\\TS3W.exe on box-zed', '--- end ---',
  ].join('\r\n');
  const r: Report = { file: 'crash-x.txt', text: scrub(sample, { user: 'zed', host: 'box-zed', home: 'C:\\Users\\<user>' }), fields: parse(sample) };
  const ok = (c: boolean, what: string) => { if (!c) { console.error(`self-check FAILED: ${what}`); process.exit(1); } };
  ok(r.fields.title === '[crash] TS3W+0x23456 (ts3native 0.36.7)' && r.fields['signature id'] === 'crashsig-0a1b2c3d', 'parse header');
  ok(!/zed/i.test(r.text) && r.text.includes('host <home>\\Steam\\TS3W.exe on <machine>'), 'scrub');
  ok(scrub('x /Users/bob/y E:\\USERS\\carol\\z', { user: '', host: '', home: '' }) === 'x /Users/<user>/y E:\\Users\\<user>\\z', 'scrub \\Users\\ rule');
  ok(title(r) === r.fields.title && SIG_RE.test(body(r)) && body(r).length <= BODY_MAX, 'title and body');
  const big: Report = { ...r, text: r.text + 'x'.repeat(100000) };
  ok(body(big).length <= BODY_MAX && body(big).includes('[cut here'), 'body cut');
  const url = issueUrl(r);
  ok(url.length <= URL_MAX && url.includes('template=crash.yml') && url.includes('signature=crashsig-0a1b2c3d') && url.includes('cut%3A+please+attach'), 'url');
  const yml = readFileSync(resolve(import.meta.dirname, '..', '.github', 'ISSUE_TEMPLATE', 'crash.yml'), 'utf8');
  for (const id of ['signature', 'version', 'report']) ok(new RegExp(`^\\s+id: ${id}$`, 'm').test(yml), `crash.yml field ${id}`);
  // Error 12 report
  const log = [
    'ts3native 0.36.86 2026-10-26 09:00:00', 'route async: plan 12 ms', 'host C:\\Users\\<user>\\x on box-zed',
    'diag: packages (first 200 names): My CC Set.package, cc-1.package, Zed Hair (2).package',
    'diag: script mods (S3SA packages): NRaas_Overwatch.package', 'diag: Documents in \\\\box-zed\\share\\Docs, mail zed.doe@example.com',
    'diag: mem #3 periodic priv=1839 big=1275 t=1534',
    'allocsites: #3 periodic live r=1 k=map at=d3d9.dll+0x4A1C2 from=d3d9.dll+0x3B7E0 MB=96.00 n=6 dMB=+16.00 dN=+1 aMB=812.0 fMB=716.0',
    ...Array.from({ length: 30 }, (_, i) => `noise ${i}`), 'loading C:\\Mods\\Packages\\secret.package failed',
  ].join('\r\n');
  const zed = { user: 'zed', host: 'box-zed', home: 'C:\\Users\\<user>' };
  const mt = memoryText(log, false, zed), mn = memoryText(log, true, zed);
  ok(!/zed|box|example\.com|My CC|Hair|NRaas_|secret|\.package/i.test(mt), `memory scrub: ${mt}`);
  ok(mt.includes('<email>') && mt.includes('\\\\<machine>\\') && !mt.includes('route async') && !mt.includes('noise 0') && mt.includes('noise 29'), 'memory keep + tail');
  const pk = /^diag: packages \(first 200 names\): (pkg-[0-9a-f]{8}), (pkg-[0-9a-f]{8}), (pkg-[0-9a-f]{8})$/m.exec(mt);
  ok(!!pk && pk[1] === memoryText('diag: x: my cc set.package', false, zed).match(/pkg-[0-9a-f]{8}/)?.[0], 'package hash stable, names with spaces whole');
  ok(mn.includes('My CC Set.package, cc-1.package') && !/zed\.doe|box-zed/i.test(mn), 'memory --names keeps package names only');
  ok(/^allocsites: #3 periodic live r=1 k=map at=d3d9\.dll\+0x4A1C2 /m.test(mt) && /^diag: mem #3 periodic priv=1839 /m.test(mt), 'memory lines unchanged for memory-report.ts');
  ok(memVersion(log) === '0.36.86' && memBody(mt, '0.36.86').includes(MEM_MARK), 'memory version + marker');
  const mbig = memBody(mt + 'allocsites: x\n'.repeat(9000), '0.36.86');
  ok(mbig.length <= BODY_MAX && mbig.includes('[cut here') && mbig.includes('ts3native 0.36.86'), 'memory body cut keeps start');
  const mu = memUrl(mt + 'allocsites: x\n'.repeat(3000), '0.36.86');
  ok(mu.length <= URL_MAX && mu.includes('template=memory.yml') && mu.includes('cut%3A+please+attach'), 'memory url');
  const myml = readFileSync(resolve(import.meta.dirname, '..', '.github', 'ISSUE_TEMPLATE', 'memory.yml'), 'utf8');
  for (const id of ['version', 'report']) ok(new RegExp(`^\\s+id: ${id}$`, 'm').test(myml), `memory.yml field ${id}`);
  console.log('report-crash self-check ok');
}

async function main() {
  const { values } = parseArgs({ options: {
    dir: { type: 'string' }, repo: { type: 'string', default: REPO }, browser: { type: 'boolean' }, list: { type: 'boolean' }, 'self-check': { type: 'boolean' },
    memory: { type: 'boolean' }, log: { type: 'string' }, names: { type: 'boolean' },
  } });
  if (values['self-check']) return selfCheck();
  if (values.memory) return memoryMain(values);
  const dir = values.dir ?? join(documents(), 'Electronic Arts', 'The Sims 3', 'ts3native-crashes');
  if (!existsSync(dir)) {
    console.log(`No crash folder at ${dir}. If your Documents folder has another name, the reports are in Game\\Bin\\ts3native-crashes: pass --dir.`);
    return;
  }
  const stateFile = join(dir, 'reported.json');
  const state: Record<string, string> = existsSync(stateFile) ? JSON.parse(readFileSync(stateFile, 'utf8')) : {};
  const fresh = readdirSync(dir).filter(n => /^crash-.*\.txt$/.test(n) && !state[n]).sort();
  if (!fresh.length) { console.log(`No new crash reports in ${dir}.`); return; }
  console.log(`${fresh.length} new crash report(s) in ${dir}:`);
  const reports = fresh.map(file => {
    const raw = readFileSync(join(dir, file), 'utf8');
    return { file, text: scrub(raw), fields: parse(raw) } as Report;
  });
  for (const r of reports) console.log(`  ${r.file}  ${title(r)}`);
  if (values.list) return;
  const useGh = !values.browser && ghReady();
  const rl = createInterface({ input: process.stdin, output: process.stdout });
  try {
    for (const r of reports) {
      console.log(`\n================ ${r.file}: this is exactly what would be sent ================`);
      console.log(`Where: ${useGh ? `a GitHub issue or comment in ${values.repo}, posted as your own gh account` : `a GitHub page in your browser, prefilled; you press Submit`}`);
      console.log(`Title: ${title(r)}\n`);
      console.log(useGh ? body(r) : decodeURIComponent(new URL(issueUrl(r, values.repo)).searchParams.get('report') ?? ''));
      console.log('================ end of what would be sent ================');
      const a = (await rl.question('Send it? y = yes, n = not now, s = never ask again for this report: ')).trim().toLowerCase();
      if (a === 's') { state[r.file] = 'skipped'; writeFileSync(stateFile, JSON.stringify(state, null, 2)); continue; }
      if (a !== 'y') continue;
      if (useGh) {
        const url = sendWithGh(r, values.repo);
        console.log(`Sent: ${url}`);
        state[r.file] = url;
      } else {
        const url = issueUrl(r, values.repo);
        console.log(`Opening the prefilled page. Check it, press "Submit new issue", and attach the full file if it was cut:\n  ${join(dir, r.file)}\n${url}`);
        openUrl(url);
        state[r.file] = 'opened in browser';
      }
      writeFileSync(stateFile, JSON.stringify(state, null, 2));
    }
  } finally {
    rl.close();
  }
}

main().catch(e => { console.error(e instanceof Error ? e.message : e); process.exit(1); });
