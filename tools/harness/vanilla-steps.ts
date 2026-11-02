// Harness steps for the vanilla queue (docs/236, queues/vanilla-matrix.json, run.script); each needs the probe server and a loaded world.
//   node tools/harness/vanilla-steps.ts accept --title "<lowercase fragment>" [--minutes 30] [--after 10] [--speed ultra]
//       H-move: autopilot on, dialog_expect {title, accept:true, rules:false} armed (re-armed when a window expires) until the agent itself accepts
//       a matching dialog, then the game must stay up --after minutes. No match in --minutes: UNKNOWN (exit 1), nothing else accepted.
//       --all-seconds flow: no offer in --minutes is "N/A: not triggered" (exit 1; queue.ts row N/A, never PASS): the offer never appeared,
//       e.g. Steam, where the move offer was never seen (docs/236 Round 9).
//       --all-seconds N (Round 6): the first match starts dialog_expect accept_all for N s (every dialog of the move flow accepted, each click logged);
//       PASS needs the household to have moved (mode exit / other household / out of the world) and the game up --after minutes after the move.
//       Offer taken but no move: UNKNOWN with the clicks. A crash after the accept is a RESULT line (crash ledger triage), the queue row shows CRASH.
//   node tools/harness/vanilla-steps.ts state-cmd     H-fault #2: buy_open, buy_close, enter_live_mode; the queue's mode guard must stay quiet
//   node tools/harness/vanilla-steps.ts kill-game [--after 60]   H-fault #1: kills the game; the queue must FAIL the job (never PASS)
//   node tools/harness/vanilla-steps.ts --self-check
// Accept and expiry lines come from the queue's click log (TS3_CLICKS_LOG, vanilla), else ts3native.log, else this script's own
// `autopilot` poll (run by hand, nobody else reads `cleared`).
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseArgs } from 'node:util';
import { GUARD_MS, stallGuard } from './game-alive.ts';
import { BIN, GAME_EXE, isSteamGame } from './game-exe.ts';

const { values, positionals } = parseArgs({ allowPositionals: true, options: {
  title: { type: 'string' }, 'all-seconds': { type: 'string' }, minutes: { type: 'string', default: '30' }, after: { type: 'string' }, speed: { type: 'string', default: 'ultra' },
  port: { type: 'string', default: '47891' }, 'self-check': { type: 'boolean' },
} });
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const log = (m: string) => console.log(`${new Date().toISOString().slice(11, 19)} ${m}`);
async function agent(cmd: string, args?: object): Promise<any> {
  const q = new URLSearchParams({ cmd, timeout: '8000' });
  if (args) q.set('args', JSON.stringify(args));
  try { return await (await fetch(`http://127.0.0.1:${values.port}/agent?${q}`, { signal: AbortSignal.timeout(20000) })).json(); } catch (e) { return { ok: false, error: String(e) }; }
}

// pure (self-check): accept and expiry entries in click lines, in either form (`cleared` text or the ts3native `ap click:` line)
export const acceptLines = (lines: string[]) => lines.filter(l => /dialog_expect accept: /.test(l));
export const expiryLines = (lines: string[]) => lines.filter(l => /dialog_expect expired \(no match\)/.test(l));

// pure (self-check): did the household move? Baseline snapshot vs now (world flag, sub state, active household)
export type Snap = { inWorld: boolean; sub: string | null; hh: string | null };
export const movedWhy = (base: Snap, cur: Snap): string => !cur.inWorld ? 'out of the world' : cur.sub !== base.sub ? `sub state ${base.sub} -> ${cur.sub}` : cur.hh !== base.hh ? `household ${base.hh} -> ${cur.hh}` : '';
export const allLines = (lines: string[]) => lines.filter(l => /dialog_expect accept_all: /.test(l));
export const allEnded = (lines: string[]) => lines.some(l => /dialog_expect accept_all ended: /.test(l));
// docs/236 Round 9: an offer that never appeared is not a result either way (Steam showed no "new towns" offer in the vanilla matrix)
export const notTriggered = (title: string, minutes: number, windows: number, steam: boolean) =>
  `N/A: not triggered: no dialog matching "${title}" in ${minutes} min (${windows} windows); the offer never appeared${steam ? ' (Steam edition: never seen there)' : ''}; nothing accepted`;
const snap = async (): Promise<Snap | null> => { const g = await agent('game_state'); return g.ok && g.result ? { inWorld: !!g.result.in_world, sub: g.result.sub_state ?? null, hh: g.result.active_household ?? null } : null; };
const triageNow = (since: string) => { try { return execFileSync(process.execPath, [join(import.meta.dirname, 'crash-ledger.ts'), 'triage', '--since', since], { timeout: 60000 }).toString().trim().split(/\n\s*\n/).at(-1) ?? ''; } catch (e) { return `crash-ledger triage failed: ${String(e).split('\n')[0]}`; } };

// the ts3agent keeps every click line (dialog_expect {status:true}, never drained): the queue's `autopilot` poll drains `cleared`, and a poll the game answered
// too late (world load) loses its lines (Round 6 vh-move run 1)
let histSeen = 0;
async function histNew(): Promise<string[]> {
  const r = await agent('dialog_expect', { status: true });
  if (!r.ok || !Array.isArray(r.result?.history)) return [];
  const h: string[] = r.result.history.map(String), n = h.slice(Math.min(histSeen, h.length));
  histSeen = h.length;
  return n;
}

let readOff = 0;
async function newClicks(): Promise<string[]> {
  const file = process.env.TS3_CLICKS_LOG || (existsSync(join(BIN, 'ts3native.log')) ? join(BIN, 'ts3native.log') : '');
  if (!file) return ((await agent('autopilot')).result?.cleared ?? []).map(String);
  if (!existsSync(file)) return [];
  const b = readFileSync(file), out = b.subarray(Math.min(readOff, b.length)).toString('latin1').split(/\r?\n/).filter(Boolean);
  readOff = b.length;
  return out;
}


// Round 6 vh-move: wait for the offer, accept every dialog of the flow for --all-seconds, then watch for the move and --after minutes of survival
async function acceptFlow(title: string, minutes: number, after: number, allSec: number) {
  const started = new Date().toISOString();
  await histNew();
  await agent('autopilot', { enabled: true, speed: values.speed, dialogs: true });
  const base = await snap();
  if (!base || !base.inWorld) throw new Error(`no world to start from: ${JSON.stringify(base)}`);
  const end = Date.now() + minutes * 60000, guard = stallGuard('offer wait', { port: Number(values.port) });
  const clicks: string[] = [], acc = () => clicks.filter(l => /accept_all: /.test(l)).map(l => l.replace(/^.*?dialog_expect accept_all: /, '')).join(' | ') || 'none';
  let armedUntil = 0, windows = 0, triggered = false;
  while (!triggered && Date.now() < end) {
    if (Date.now() >= armedUntil) {
      const sec = Math.min(900, Math.ceil((end - Date.now()) / 1000));
      const r = await agent('dialog_expect', { title, accept_all: true, all_seconds: allSec, seconds: sec });
      if (!r.ok || r.result?.accept_all !== true) throw new Error(`dialog_expect accept_all not supported by this ts3agent: ${JSON.stringify(r).slice(0, 200)}`);
      armedUntil = Date.now() + sec * 1000; windows++;
      log(`dialog_expect armed: "${title}", accept_all ${allSec} s after the first match, waiting ${sec} s (window ${windows})`);
    }
    await sleep(5000);
    const c = await histNew(); clicks.push(...c.filter(l => /dialog_expect/.test(l)));
    for (const e of expiryLines(c)) log(`agent: ${e.replace(/^.*?dialog_expect/, 'dialog_expect')}`);
    triggered = allLines(c).length > 0;
    const why = await guard();
    if (why) { console.log(`FAIL: ${why}`); process.exit(1); }
  }
  if (!triggered) { console.log(notTriggered(title, minutes, windows, isSteamGame())); process.exit(1); }
  log('offer shown, accept_all window started');
  // move phase: until accept_all ended and the move was seen (or allSec + 120 s); a dead game here is the RESULT
  const g1 = stallGuard('move flow', { port: Number(values.port), liveOnly: true });
  let movedAt = 0, movedBy = '', ended = false;
  const flowEnd = Date.now() + (allSec + 120) * 1000;
  while ((!ended || !movedAt) && Date.now() < flowEnd) {
    await sleep(5000);
    const c = await histNew(); clicks.push(...c.filter(l => /dialog_expect/.test(l)));
    for (const l of allLines(c)) log(`click: ${l.replace(/^.*?dialog_expect accept_all: /, '')}`);
    ended ||= allEnded(c);
    if (!movedAt) { const s = await snap(), w = s ? movedWhy(base, s) : ''; if (w) { movedAt = Date.now(); movedBy = w; log(`moved: ${w}`); } }
    const why = await g1();
    if (why) return crashResult(started, why, movedBy, acc());
  }
  if (!movedAt) {
    console.log(`UNKNOWN: offer accepted, household did not move (no mode exit, same household ${base.hh}); clicks: ${acc()}; the move may need a world pick in the UI (not clicked)`);
    process.exit(1);
  }
  const g2 = stallGuard('after the move', { port: Number(values.port) });
  for (const stop = movedAt + after * 60000; Date.now() < stop;) {
    await sleep(5000);
    // no autopilot call here: it drains `cleared`, which the queue's mode guard writes to the click log (it also restarts the task after a world switch)
    const why = await g2();
    if (why) return crashResult(started, why, movedBy, acc());
  }
  console.log(`PASS: moved (${movedBy}), game up ${after} min after the move, no crash; clicks: ${acc()}`);
}
function crashResult(since: string, why: string, movedBy: string, clicks: string) {
  const t = triageNow(since).replace(/\s+/g, ' ').slice(0, 600);
  console.log(`RESULT: vanilla game died ${movedBy ? `after the move (${movedBy})` : 'in the move flow before a move was seen'}: ${why.split(';')[0]}; crash ledger: ${t}; clicks: ${clicks}`);
  process.exit(1); // the queue row shows CRASH with its own triage: a result of the test, not a harness failure
}

async function accept() {
  const title = values.title?.toLowerCase(), minutes = Number(values.minutes), after = Number(values.after ?? 10);
  if (!title) throw new Error('accept needs --title');
  if (values['all-seconds']) return acceptFlow(title, minutes, after, Number(values['all-seconds']));
  await newClicks(); // skip what is already there
  await agent('autopilot', { enabled: true, speed: values.speed, dialogs: true });
  const end = Date.now() + minutes * 60000, guard = stallGuard('accept wait', { port: Number(values.port) });
  let windows = 0, armedUntil = 0, got = '';
  while (!got && Date.now() < end) {
    if (Date.now() >= armedUntil) {
      const sec = Math.min(900, Math.ceil((end - Date.now()) / 1000));
      const r = await agent('dialog_expect', { title, accept: true, rules: false, seconds: sec }); // rules off: the move rule declines this offer
      if (!r.ok || r.result?.accept !== true) throw new Error(`dialog_expect accept not supported by this ts3agent: ${JSON.stringify(r).slice(0, 200)}`);
      armedUntil = Date.now() + sec * 1000; windows++;
      log(`dialog_expect armed: "${title}", accept, ${sec} s (window ${windows})`);
    }
    await sleep(5000);
    const c = await newClicks();
    for (const e of expiryLines(c)) log(`agent: ${e.replace(/^.*?dialog_expect/, 'dialog_expect')}`);
    got = acceptLines(c).at(-1) ?? '';
    const why = await guard();
    if (why) { console.log(`FAIL: ${why}`); process.exit(1); }
  }
  if (!got) { console.log(`UNKNOWN: accept: no dialog matching "${title}" in ${minutes} min (${windows} windows); nothing accepted`); process.exit(1); }
  log(`accepted: ${got.replace(/^.*?dialog_expect accept: /, '')}`);
  const g2 = stallGuard('after the accept', { port: Number(values.port) });
  for (const stop = Date.now() + after * 60000; Date.now() < stop;) {
    await sleep(5000);
    const why = await g2();
    if (why) { console.log(`FAIL: after the accept: ${why}`); process.exit(1); }
  }
  console.log(`PASS: accept: "${title}" accepted by the agent (${got.replace(/^.*?dialog_expect accept: /, '').slice(0, 160)}), game up ${after} min after`);
}

async function stateCmd() {
  // the move-offer dialog is modal and blocks buy_open (Round 8): autopilot dialogs answer it by rule, as in the other jobs
  await agent('autopilot', { enabled: true, speed: values.speed, dialogs: true });
  // buy_open/buy_close are queued game tasks (Buy took >20 s on the EA laptop): wait on cas_state, not a fixed sleep (Round 8)
  const reached = (r: any, cmd: string) => cmd === 'buy_open' ? !!r?.result?.buy_catalog : r?.result?.state === 'LiveModeState'; // agent() returns {id, ok, result}
  for (const [cmd, waitS] of [['buy_open', 15], ['buy_close', 15], ['enter_live_mode', 90]] as const) {
    const r = await agent(cmd);
    log(`${cmd}: ${r.ok ? 'ok ' + JSON.stringify(r).slice(0, 300) : `error ${r.error}`}`);
    if (!r.ok) { console.log(`FAIL: state-cmd: ${cmd}: ${r.error}`); process.exit(1); }
    await sleep(waitS * 1000);
    if (cmd === 'enter_live_mode') break;
    const t0 = Date.now();
    for (let st = await agent('cas_state'); !reached(st, cmd); st = await agent('cas_state')) {
      if (Date.now() - t0 > 180000) {
        log(`${cmd}: last cas_state ${JSON.stringify(st).slice(0, 700)}; pending_dialogs ${JSON.stringify(await agent('pending_dialogs')).slice(0, 500)}`);
        console.log(`FAIL: state-cmd: ${cmd}: mode not reached after 180 s`); process.exit(1);
      }
      await sleep(5000);
    }
    log(`${cmd}: mode reached after ${Math.round((Date.now() - t0) / 1000) + waitS} s`);
  }
  console.log(`PASS: state-cmd: harness state commands ran; the mode guard must not have stopped this job (${3 * GUARD_MS / 1000} s of checks)`);
}

async function killGame() {
  await sleep(Number(values.after ?? 60) * 1000);
  log(`fault: taskkill /F /IM ${GAME_EXE}`);
  try { execFileSync('taskkill', ['/F', '/IM', GAME_EXE], { stdio: 'ignore' }); } catch { /* already gone */ }
  await sleep(150000); // the queue's jobGuard ends this child within ~60 s
  console.log('FAIL: kill-game: the queue did not stop this job within 150 s of the kill');
  process.exit(1);
}

if (values['self-check']) {
  const lines = ['12:00:01 dialog_expect accept: There are new towns available -> #2 yes (accept requested)', 'ap click: x -> dialog_expect accept: #2 yes (accept requested)',
    '12:00:02 dialog_expect expired (no match): new towns available [accept]', '12:00:03 Hot Dogs -> #3 cancel (safe default)'];
  console.assert(acceptLines(lines).length === 2 && expiryLines(lines).length === 1, 'accept/expiry line match');
  if (acceptLines(lines).length !== 2 || expiryLines(lines).length !== 1) process.exit(1);
  const b: Snap = { inWorld: true, sub: 'LiveModeState', hh: 'A' };
  const sc = movedWhy(b, b) === '' && movedWhy(b, { ...b, inWorld: false }) === 'out of the world' && movedWhy(b, { ...b, hh: 'B' }).includes('A -> B') && movedWhy(b, { ...b, sub: 'CASState' }).includes('sub state')
    && allLines(['x dialog_expect accept_all: #1 ok']).length === 1 && allEnded(['dialog_expect accept_all ended: 3 clicks']) && !allEnded(['dialog_expect accept: x'])
    && /^N\/A: not triggered: .*\(Steam edition/.test(notTriggered('x', 6, 1, true)) && !/Steam/.test(notTriggered('x', 6, 1, false));
  console.assert(sc, 'moved/accept_all/N/A line match');
  if (!sc) process.exit(1);
  console.log('vanilla-steps.ts self-check OK');
} else if (positionals[0] === 'accept') await accept();
else if (positionals[0] === 'state-cmd') await stateCmd();
else if (positionals[0] === 'kill-game') await killGame();
else { console.error('usage: vanilla-steps.ts accept --title T [--minutes 30] [--after 10] | state-cmd | kill-game [--after 60] | --self-check'); process.exit(2); }
