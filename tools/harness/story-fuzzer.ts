// Story fuzzer engine (docs/171 §7): seeded parallel stories on 8-12 driven Sims while profile-world measures the window.
//   node tools/harness/profile-world.ts --tag sv-story --minutes 30 --story '{"seed":7,"stories":40}' [--no-env]
//   node tools/harness/story-fuzzer.ts --check [--content DIR]           schema + believability check (story-lint.ts) of the content
//   node tools/harness/story-fuzzer.ts --dry-run --seed 7 [--config FILE|JSON] [--census FILE] [--content DIR] [--minutes 30]
//        the plan for a seed without a game: the real scheduler against a fake game built from the census (cached file or
//        the story-census.ts fixture) on a virtual clock; same seed + same answers = same plan
//   node tools/harness/story-fuzzer.ts                                    self-check (fixture content, census and game)
// Content (piece C, tools/harness/stories/): templates/*.json (arrays, schema docs/171 §2), personas.json ([{id, household:
// {ages}, categoryBias, templateBias}]), categories.json ({weights}), worlds/*.json ({world?, categoryBias?, templateBias?}).
// A template's weight = weight x category weight x persona and world categoryBias x the longest matching templateBias prefix
// (persona of the story's lead actor, world from the census). Config (JSON, all optional): seed, stories (cap N), minutes
// (cap, else the window), driven [8,12], parallel 4, gapS [3,10] between story starts, cameraP 0.3 / modeP 0.05 per start,
// maxPrepares 20, maxSpawns 6, only [ids or prefixes], categories {cat: weight}, persona (force one), content (dir),
// censusOnly (scratch copy + world_census into the cache, no stories), travel (run travel templates; off: they unload the world).
// Randomness: the scheduler draws from rng(seed); story run k draws from rng(seed, k), so a story replays from (seed, k).
// Log <tag>-story.jsonl (docs/171 §5): a step line per agent call of a story, plus kind census | prepare | skip | between
// (camera jump, build mode) | scratch | coverage lines. Every call also lands in <tag>-calls.jsonl through profile-world's ag.
// Safety: the loaded save is copied to Story-<save>-s<seed> (save_game by name) before anything changes; a Baseline-* save is
// refused when that copy fails; template steps never load, quit or travel home, and save_game always gets the scratch name.
import assert from 'node:assert/strict';
import { appendFileSync, existsSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { AsyncLocalStorage } from 'node:async_hooks';
import { checkStep, runStep, type LoopStep } from './repro-loop.ts';
import { FIXTURE, fromAgent, getCensus, isAny, KINDS, MOON, norm, normalize, needStatus, hasNeed, samplesOf, type Census, type Kind, type Need } from './story-census.ts';
import { lintContent } from './story-lint.ts'; // cycle is fine: neither file uses the other at load time
import { runDays, withDays, type DaysConfig } from './story-day.ts';
import { createHousehold } from './story-households.ts';

export const CATEGORIES = ['normal', 'lifestage', 'chaos', 'occult', 'world'];
export const AGES = ['Baby', 'Toddler', 'Child', 'Teen', 'YoungAdult', 'Adult', 'Elder'];
const PICKS = ['driven', 'townie', 'new'];
const BLOCKED = new Set(['load_save', 'load_world', 'fresh_world', 'quit_game', 'enter_live_mode']);
const TARGET_KINDS: Kind[] = ['object', 'shop', 'rabbithole', 'venue', 'age', 'occult', 'pet', 'skillclass'];
// a travel story unloads the world: every other running story loses its Sims and the loading screen trips profile-world's clock
// guard (story-smoke 2026-10-23, docs/171 §13); skipped unless the config sets travel: true (then run it with parallel: 1)
const TRAVEL = new Set(['travel_vacation', 'travel_university', 'travel_future', 'travel_home']);
// the Sim is not there now (despawned townie, at work, in a rabbit hole, another world) or lacks the motive: a skip, not a failure
// "not in live mode": the agent refuses Sim and object changes outside live mode (docs/171 §14)
export const UNAVAILABLE = /no sim named|not (currently )?in the world|not instantiated|has no motive|^busy: |^no enabled interactions|^not in live mode/;
// steps that leave live mode: the story owns the game from there until its end (docs/171 §14)
export const leavesLive = (s: { cmd: string; args?: any }) => s.cmd === 'buy_open' || s.cmd === 'cas_open' || s.cmd === 'build_mode' && s.args?.on !== false;
// a pie label matches the whole entry or any path segment ("Friendly... / Chat" matches "Chat")
export const labelHit = (entry: string, want: string) => String(entry).toLowerCase().split(' / ').some((_, i, a) => a.slice(i).join(' / ').startsWith(want));
export const STORIES_DIR = resolve(import.meta.dirname, 'stories');

// family: the role's Sim comes from the same household as that (earlier) role's Sim (a parent of the child role)
export type Role = { role: string; age?: string[]; gender?: 'Female' | 'Male'; occult?: string | string[] | null; pick?: 'driven' | 'townie' | 'new'; family?: string };
export type TStep = { cmd: string; args?: any; actor?: string; target?: string | { find: Kind; value?: string | string[] }; label?: string; waitS?: number; waitUntil?: any; minS?: number; maxS?: number; optional?: boolean };
// death: the role whose Sim the template may kill; the day planner's death guard leaves that one Sim alone (docs/171 §12)
export type Template = { id: string; category: string; weight?: number; actors: Role[]; needs?: Need[]; steps: TStep[]; cooldownMin?: number; maxConcurrent?: number; death?: string; realism?: 'natural' | 'cheat' | 'stress'; note?: string };
type Bias = { categoryBias?: Record<string, number>; templateBias?: Record<string, number> };
type Persona = Bias & { id: string; household?: { ages?: string[] } };
export type Content = { templates: Template[]; personas: Persona[]; categories: Record<string, number>; worlds: Record<string, Bias> };
// drive: household (default, docs/179 R-HH-4: a player orders only the active household; townies fill only roles that never
// act) | world (stress: seeded townies are driven too, spawned when short; the pre-2026-10-23 behaviour)
export type StoryConfig = { householdLot?: string; seed?: number; stories?: number; minutes?: number; driven?: [number, number]; parallel?: number; gapS?: [number, number]; cameraP?: number; modeP?: number; maxPrepares?: number; maxSpawns?: number; only?: string[]; categories?: Record<string, number>; persona?: string; content?: string; replay?: string; from?: number; to?: number; censusOnly?: boolean; days?: boolean | DaysConfig; travel?: boolean; realism?: string[]; drive?: 'household' | 'world'; household?: string; householdFunds?: number; personaOf?: Record<string, string> };
// the role a step orders (its actor, or $actor.<role> as the sim argument)
export const actorOf = (s: TStep) => s.actor ?? /^\$actor\.([A-Za-z0-9_-]+)$/.exec(String(s.args?.sim ?? ''))?.[1];

// ---- seeded randomness (mulberry32); story k gets its own stream
export function rng(seed: number, k = 0) {
  let a = (seed * 0x9E3779B1 + k * 0x85EBCA77) >>> 0 || 1;
  const next = () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
  return {
    next, int: (lo: number, hi: number) => lo + Math.floor(next() * (hi - lo + 1)),
    pick: <T>(xs: T[]): T | undefined => xs.length ? xs[Math.floor(next() * xs.length)] : undefined,
    weighted: <T>(xs: T[], w: (x: T) => number): T | undefined => {
      const ws = xs.map(x => Math.max(0, w(x))), sum = ws.reduce((a, b) => a + b, 0);
      if (!(sum > 0)) return undefined;
      let r = next() * sum;
      for (let i = 0; i < xs.length; i++) if ((r -= ws[i]) < 0) return xs[i];
      return xs[xs.length - 1];
    },
  };
}

// ---- schema (docs/171 §2): every error, not just the first
export function checkTemplates(list: any, where = 'templates'): string[] {
  const errs: string[] = [];
  if (!Array.isArray(list)) return [`${where}: must be an array of templates`];
  const ids = new Set<string>();
  for (const [n, t] of list.entries()) {
    const at = `${where}[${n}]${t?.id ? ` ${t.id}` : ''}`, e = (m: string) => errs.push(`${at}: ${m}`);
    if (typeof t?.id !== 'string' || !/^[a-z0-9][a-z0-9-]*$/.test(t.id)) { e('id must be kebab-case'); continue; }
    if (ids.has(t.id)) e('duplicate id'); ids.add(t.id);
    if (!CATEGORIES.includes(t.category)) e(`category must be one of ${CATEGORIES.join('|')}`);
    if (t.weight !== undefined && !(typeof t.weight === 'number' && t.weight >= 0)) e('weight must be a number >= 0');
    if (!Array.isArray(t.actors)) e('actors must be a list (empty = a world story without a lead Sim)');
    const roles = new Set<string>();
    for (const a of t.actors ?? []) {
      if (typeof a?.role !== 'string' || !a.role) { e('actor without role'); continue; }
      if (roles.has(a.role)) e(`role ${a.role} twice`); roles.add(a.role);
      if (a.age !== undefined && !(Array.isArray(a.age) && a.age.every((x: any) => AGES.includes(x)))) e(`role ${a.role}: age must list ${AGES.join('|')}`);
      if (a.occult !== undefined && a.occult !== null && !(typeof a.occult === 'string' || Array.isArray(a.occult))) e(`role ${a.role}: occult must be null, a name or a list`);
      if (a.pick !== undefined && !PICKS.includes(a.pick)) e(`role ${a.role}: pick must be ${PICKS.join('|')}`);
      if (a.gender !== undefined && a.gender !== 'Female' && a.gender !== 'Male') e(`role ${a.role}: gender must be Female|Male`);
      if (a.pick === 'new' && !a.age?.length) e(`role ${a.role}: pick new needs an age`);
      if (a.family !== undefined && (!roles.has(a.family) || a.family === a.role)) e(`role ${a.role}: family must name an earlier role`);
    }
    if (t.death !== undefined && !roles.has(t.death)) e(`death must name a role (the Sim the template may kill)`);
    for (const nd of t.needs ?? []) {
      if (!KINDS.includes(nd?.kind)) e(`need kind ${nd?.kind} not one of ${KINDS.join('|')}`);
      if (nd?.value !== undefined && !(typeof nd.value === 'string' || Array.isArray(nd.value) && nd.value.every((v: any) => typeof v === 'string'))) e(`need ${nd.kind}: value must be a string or a list`);
      if (nd?.prepare !== undefined && typeof nd.prepare !== 'boolean') e(`need ${nd.kind}: prepare must be a boolean`);
    }
    if (t.needs !== undefined && !Array.isArray(t.needs)) e('needs must be a list');
    if (!Array.isArray(t.steps) || !t.steps.length) { e('steps must be a non-empty list'); continue; }
    for (const [i, s] of t.steps.entries()) {
      const se = (m: string) => e(`step ${i} ${s?.cmd ?? ''}: ${m}`);
      if (typeof s?.cmd !== 'string' || !/^[a-z_0-9]+$/.test(s.cmd)) { se('cmd must be an agent command name'); continue; }
      if (BLOCKED.has(s.cmd)) se('not allowed in a story (loads, quits or leaves the world)');
      if (s.actor !== undefined && !roles.has(s.actor)) se(`actor ${s.actor} is not a role`);
      if ((s.cmd === 'interact' || s.cmd === 'any_interaction') && !s.actor) se('needs an actor');
      if (s.cmd === 'interact' && (typeof s.label !== 'string' || !s.label)) se('interact needs a label');
      if (s.target !== undefined && typeof s.target !== 'string' && !(s.target && TARGET_KINDS.includes(s.target.find))) se(`target must be a string or {find: ${TARGET_KINDS.join('|')}, value}`);
      if (s.args !== undefined && (typeof s.args !== 'object' || s.args === null)) se('args must be an object');
      if (s.waitS !== undefined && !(typeof s.waitS === 'number' && s.waitS >= 0)) se('waitS must be >= 0');
      if (s.waitUntil !== undefined) try { checkStep({ ...s, note: 'story' }); } catch (x: any) { se(x.message); }
      for (const m of JSON.stringify(s.args ?? {}).matchAll(/\$actor\.([A-Za-z0-9_-]+)/g)) if (!roles.has(m[1])) se(`$actor.${m[1]} is not a role`);
    }
    if (t.cooldownMin !== undefined && !(typeof t.cooldownMin === 'number' && t.cooldownMin >= 0)) e('cooldownMin must be >= 0');
    if (t.maxConcurrent !== undefined && !(Number.isInteger(t.maxConcurrent) && t.maxConcurrent >= 1)) e('maxConcurrent must be an integer >= 1');
  }
  return errs;
}

const readJson = (f: string) => JSON.parse(readFileSync(f, 'utf8'));
export function loadContent(dir = STORIES_DIR): { content: Content; errors: string[] } {
  const errors: string[] = [], templates: Template[] = [];
  const tdir = join(dir, 'templates');
  for (const f of existsSync(tdir) ? readdirSync(tdir).filter(f => f.endsWith('.json')).sort() : []) {
    try { const l = readJson(join(tdir, f)); errors.push(...checkTemplates(l, `templates/${f}`)); if (Array.isArray(l)) templates.push(...l); } catch (x: any) { errors.push(`templates/${f}: ${x.message}`); }
  }
  const seen = new Set<string>();
  for (const t of templates) { if (seen.has(t.id)) errors.push(`template id ${t.id} in two files`); seen.add(t.id); }
  const opt = (f: string, d: any) => { try { return existsSync(join(dir, f)) ? readJson(join(dir, f)) : d; } catch (x: any) { errors.push(`${f}: ${x.message}`); return d; } };
  const personas: Persona[] = opt('personas.json', []);
  if (!Array.isArray(personas) || personas.some(p => typeof p?.id !== 'string')) errors.push('personas.json: must be a list of {id, ...}');
  const categories: Record<string, number> = opt('categories.json', {}).weights ?? {};
  for (const [k, v] of Object.entries(categories)) if (!CATEGORIES.includes(k) || !(Number(v) >= 0)) errors.push(`categories.json: bad weight ${k}=${v}`);
  const worlds: Record<string, Bias> = {};
  const wdir = join(dir, 'worlds');
  for (const f of existsSync(wdir) ? readdirSync(wdir).filter(f => f.endsWith('.json')) : []) {
    try { const w = readJson(join(wdir, f)); worlds[norm(w.world ?? basename(f, '.json'))] = w; } catch (x: any) { errors.push(`worlds/${f}: ${x.message}`); }
  }
  const content = { templates, personas: Array.isArray(personas) ? personas : [], categories, worlds };
  if (!errors.length) errors.push(...lintContent(content).map(f => `believability: ${f}`)); // story-lint.ts (docs/171 §11)
  return { content, errors };
}

// longest prefix (or exact id) of the bias table wins: "daily-work" beats "daily-"
export const prefixBias = (bias: Record<string, number> | undefined, id: string) => {
  let best = '', v = 1;
  for (const [k, x] of Object.entries(bias ?? {})) if (id.startsWith(k) && k.length > best.length) { best = k; v = Number(x); }
  return v;
};

// busy = in a story run; blocked = the day planner has the Sim at work, school, asleep or in a pursuit (docs/171 §12)
type Sim = { id: string; name: string; age: string; female?: boolean; occult: string[]; species?: string; active: boolean; persona?: Persona; busy?: string; blocked?: string; lot?: string; household?: string };
export type StoryIO = {
  ag: (cmd: string, args?: object, timeoutMs?: number) => Promise<any>;
  sleep: (ms: number) => Promise<unknown>;
  now: () => number;
  halted: () => boolean;   // the window is over (profile-world: stop rule or cap)
  log: (line: object) => void;
  print?: (line: string) => void;
  envFree?: boolean;       // profile-world --no-env: no 30 s test_env guard, so season / moon prepares stay
  census?: Census;         // dry run / self-check: skip world_census
};
export type Coverage = Record<string, { runs: number; ok: number; failed: number; skipped: Record<string, number>; labelMiss: Record<string, number> }>;

// The save every later change goes to: a Story-* copy of the loaded save (by name, so the loaded file is never written)
async function scratchGuard(io: StoryIO, seed: number): Promise<{ base: string; scratch: string }> {
  const st = await io.ag('game_state');
  const base = String(st?.result?.save_name ?? String(st?.result?.load_file ?? '').split(/[\\/]/).pop()?.replace(/\.sims3$/i, '') ?? '');
  if (!base) throw new Error('story: no save name in game_state; refusing to run on an unknown save');
  if (/^Story-/i.test(base)) return { base: base.replace(/^Story-(.*)-s\d+$/i, '$1'), scratch: base };
  const scratch = `Story-${base.replace(/[^A-Za-z0-9_-]/g, '_')}-s${seed}`, t = io.now();
  let r: any;
  for (let i = 0; i < 30; i++) { // a save still finishing or a dialog: retried for ~30 s, as save-matrix does
    r = await io.ag('save_game', { name: scratch }, 30000);
    if (r?.ok || !/in progress|cannot save|dialog/.test(String(r?.error))) break;
    await io.sleep(1000);
  }
  io.log({ kind: 'scratch', t, ms: io.now() - t, from: base, save: scratch, ok: !!r?.ok, ...(!r?.ok && { error: String(r?.error) }) });
  if (!r?.ok) throw new Error(`story: could not copy ${base} to ${scratch} (${r?.error}); refusing to change ${/^Baseline-/i.test(base) ? 'a Baseline save' : 'the loaded save'}`);
  return { base, scratch };
}

export async function runStories(io: StoryIO, cfg: StoryConfig, content: Content): Promise<Coverage> {
  const seed = cfg.seed ?? 1, R = rng(seed), t0 = io.now(), print = io.print ?? (() => {});
  const capMs = cfg.minutes ? cfg.minutes * 60000 : Infinity, maxStories = cfg.stories ?? Infinity;
  let stop = ''; // set when the game cannot be brought back to live mode (ensureLive): every story and the day planner end
  const timeUp = () => io.halted() || io.now() - t0 >= capMs || !!stop;
  const { base, scratch } = await scratchGuard(io, seed);
  let census = io.census, cached = true;
  if (!census) ({ census, cached } = await getCensus(io.ag, base, !!cfg.censusOnly));
  const C = census!;
  io.log({ kind: 'census', t: io.now(), seed, world: C.world, save: scratch, base, cached, kinds: Object.fromEntries(KINDS.map(k => [k, C.have[k].size])) });
  if (cfg.censusOnly) { print(`story: census only (${C.world}, ${cached ? 'cached' : 'fresh'})`); return {}; }
  const ctx = { seed, world: C.world, save: scratch };
  const cov: Coverage = Object.fromEntries(content.templates.map(t => [t.id, { runs: 0, ok: 0, failed: 0, skipped: {}, labelMiss: {} }]));
  const skip = (t: Template, why: string, extra: object = {}) => {
    const first = !cov[t.id].skipped[why];
    cov[t.id].skipped[why] = (cov[t.id].skipped[why] ?? 0) + 1;
    if (first) io.log({ kind: 'skip', t: io.now(), story: t.id, why, ...extra, ...ctx }); // once per reason; the count goes to coverage
  };

  // templates whose needs hold now or can be prepared
  const wbias = content.worlds[norm(C.world)] ?? {};
  const cats = { ...content.categories, ...cfg.categories };
  const only = cfg.only ?? [];
  const eligible = content.templates.filter(t => {
    if (only.length && !only.some(o => t.id === o || t.id.startsWith(o))) return false;
    if (!cfg.travel && t.steps.some(s => TRAVEL.has(s.cmd))) { skip(t, 'travel leaves the world (config travel: true)'); return false; }
    if (!(cfg.realism ?? ['natural', 'cheat']).includes(t.realism ?? 'natural')) { skip(t, `realism ${t.realism} (config realism, docs/179)`); return false; }
    const miss = (t.needs ?? []).find(n => needStatus(C, n, { envFree: io.envFree }) === 'missing');
    if (miss) { skip(t, `missing ${miss.kind}${miss.value ? ` ${[miss.value].flat().join('|')}` : ''}`); return false; }
    return true;
  });

  // driven Sims: active household + townies, spawned when short
  const occultOf = new Map<string, string[]>((C.raw?.sims ?? []).map((s: any) => [String(s.id), [s.occult ?? []].flat().map(String)]));
  const femaleOf = new Map<string, boolean | undefined>((C.raw?.sims ?? []).map((s: any) => [String(s.id), s.female]));
  const householdOf = new Map<string, string>((C.raw?.sims ?? []).filter((s: any) => s.household != null).map((s: any) => [String(s.id), String(s.household)]));
  let activeHousehold = (C.raw?.sims ?? []).find((s: any) => s.active && s.household != null)?.household;
  const asSim = (s: any, active: boolean): Sim => ({ id: String(s.id), name: String(s.name), age: String(s.age), female: femaleOf.get(String(s.id)), occult: occultOf.get(String(s.id)) ?? [], species: s.species, active, lot: s.lot, household: householdOf.get(String(s.id)) });
  // household id -> home lot id (census households); townies only from households with a home: Tourist, service, role and
  // homeless households come and go (story-smoke 2026-10-23: a Tourist despawned between steps, a bouncer and a mixologist
  // never took a story interaction); a census without households (fixture) keeps every Sim
  const homeLot: Record<string, string> | undefined = C.raw?.homeLot;
  const listSims = async (scope: string) => { const r = await io.ag('list_sims', { scope }); return Array.isArray(r?.result) ? r.result.filter((s: any) => s.in_world && !s.species && !s.ghost && (!homeLot || scope === 'household' || homeLot[householdOf.get(String(s.id)) ?? ''])) : []; };
  const [lo, hi] = cfg.driven ?? [8, 12], want = R.int(lo, hi), world = cfg.drive === 'world';
  const driven: Sim[] = (await listSims('household')).map((s: any) => asSim(s, true));
  const townies = async () => { const ids = new Set(driven.map(s => s.id)); const l = (await listSims('world')).filter((s: any) => !ids.has(String(s.id))); for (let i = l.length - 1; i > 0; i--) { const j = R.int(0, i); [l[i], l[j]] = [l[j], l[i]]; } return l; };
  // household mode: the other Sims of the world are only partners and targets (roles no step orders), never leads or actors
  const others: Sim[] = world ? [] : (await townies()).map((s: any) => asSim(s, false));
  if (world) {
    for (const s of (await townies()).slice(0, Math.max(0, want - driven.length))) driven.push(asSim(s, false));
    if (driven.length < want) {
      const r = await io.ag('spawn_sims', { count: want - driven.length });
      io.log({ kind: 'prepare', t: io.now(), what: 'spawn_sims', count: want - driven.length, calls: [{ cmd: 'spawn_sims', args: { count: want - driven.length } }], ok: !!r?.ok, ...(!r?.ok && { error: String(r?.error) }), ...ctx });
      for (const s of (await townies()).slice(0, want - driven.length)) driven.push(asSim(s, false));
    }
  }
  const assignPersona = (s: Sim) => {
    const fit = content.personas.filter(p => !p.household?.ages || p.household.ages.includes(s.age));
    s.persona = (cfg.persona && content.personas.find(p => p.id === cfg.persona)) || content.personas.find(p => p.id === cfg.personaOf?.[s.name]) || R.pick(fit.length ? fit : content.personas);
  };
  driven.forEach(assignPersona);
  print(`story: ${C.world}, save ${scratch}, seed ${seed}, ${driven.length} driven Sims (${driven.filter(s => s.active).length} household${world ? ', drive world: townies ordered too (stress)' : `, ${others.length} townies as partners only`}), ${eligible.length}/${content.templates.length} templates eligible`);

  // Sim targets ({find: age|occult|pet}) of running stories, by Sim id -> story run: a Sim is in one story at a time
  const claimed = new Map<string, string>();
  const deathOwned = new Map<string, string>(); // Sim id -> the death template run that owns its death (guard off for it)
  const fits = (s: Sim, r: Role) => !s.busy && !s.blocked && !claimed.has(s.id) && (!r.age?.length || r.age.includes(s.age)) && (!r.gender || s.female === (r.gender === 'Female')) &&(r.occult == null || [r.occult].flat().some(o => s.occult.some(x => norm(x) === norm(o)))) && (r.pick !== 'townie' || !s.active);
  const weightFor = (t: Template, lead: Sim) => (t.weight ?? 1) * (cats[t.category] ?? 1)
    * (lead.persona?.categoryBias?.[t.category] ?? 1) * prefixBias(lead.persona?.templateBias, t.id)
    * (wbias.categoryBias?.[t.category] ?? 1) * prefixBias(wbias.templateBias, t.id);

  // game modes (docs/171 §14): a mode switch (the between action, or a story step that opens build/buy/CAS) owns the game
  // until it is back in live mode: it waits for everyone else's in-flight calls, then holds other stories' and the day
  // planner's calls; the owner story's own calls (its AsyncLocalStorage run number) pass. Not live after 60 s = run stops.
  const runNo = new AsyncLocalStorage<number>();
  let gate: Promise<void> | null = null, gateOwner = 0, inflight = 0;
  const call = async (cmd: string, args?: object, to?: number) => {
    while (gate && runNo.getStore() !== gateOwner) await gate;
    if (stop) return { ok: false, error: stop };
    inflight++; try { return await io.ag(cmd, args, to); } finally { inflight--; }
  };
  const lockMode = async (who: number) => {
    while (gate) await gate;
    let open!: () => void;
    gate = new Promise(r => { open = r; }); gateOwner = who;
    while (inflight) await io.sleep(100);
    return () => { gate = null; gateOwner = 0; open(); };
  };
  const subState = async () => String((await io.ag('game_state', {}, 10000))?.result?.sub_state);
  const waitState = async (want: string, ms: number, retry?: () => Promise<unknown>) => {
    const end = io.now() + ms;
    for (let n = 0; ; n++) {
      const s = await subState();
      if (s === want) return true;
      if (io.now() >= end) return false;
      if (retry && n % 10 === 9) await retry(); // build_mode off / buy_close only queue the switch: ask again every ~10 s
      await io.sleep(1000);
    }
  };
  const ensureLive = async () => {
    if (await waitState('LiveModeState', 60000, () => io.ag('enter_live_mode', {}, 30000))) return true;
    stop = `game not back in live mode after 60 s (${await subState()}): stories stopped`;
    print(`FAIL: ${stop}`);
    return false;
  };
  let prepares = 0, spawns = 0;
  // an affordable product for a query ('' = any): household_funds (read) caps catalog_search's price (docs/179 R-HH-5)
  const pickProduct = async (query: string, r: ReturnType<typeof rng>, step: (cmd: string, args: object) => Promise<any>) => {
    const funds = Number((await step('household_funds', {}))?.funds);
    const hits = await step('catalog_search', { query, ...(Number.isFinite(funds) && { max_price: funds }) });
    const p = r.pick((Array.isArray(hits) ? hits : []).filter((h: any) => !h.wall));
    if (!p) throw new Error(`catalog_search ${query || 'any'}: no product the household can afford${Number.isFinite(funds) ? ` (§${funds})` : ''}`);
    return p;
  };
  // the player's buy flow (docs/179 §2.1, R-MODE-3): Buy button on the home lot, buy and place with household funds, back to
  // Live. The mode lock is taken here unless the story already owns the game in Buy mode (its own buy_open step)
  const buyInBuyMode = async (query: string, r: ReturnType<typeof rng>, step: (cmd: string, args: object) => Promise<any>) => {
    const own = !!gate && gateOwner === runNo.getStore();
    if (own && await subState() !== 'BuyModeState') throw new Error(`not in live mode: buy needs Live or Buy (${await subState()})`);
    const unlock = own ? null : await lockMode(runNo.getStore() ?? -1);
    try {
      if (!own) {
        await step('buy_open', {});
        if (!await waitState('BuyModeState', 30000)) throw new Error('buy_open: never reached Buy mode');
      }
      const p = await pickProduct(query, r, step);
      return await step('buy_object', { product: p.product, near: 'home' });
    } finally {
      if (unlock) { await step('buy_close', {}).catch(() => {}); await ensureLive(); unlock(); } // ensureLive asks again and stops the run if never live
    }
  };

  // one prepare (docs/171 §4): object = the Buy-mode flow above; age / occult / pet = make_sim (+ make_occult);
  // season / moon = test_env fixed (envFree only)
  // answer: the made Sim, the placed object's id, true (done, nothing to target) or null (failed)
  const prepare = async (n: Need & { age?: string; gender?: string }, r: ReturnType<typeof rng>, t: Template): Promise<Sim | string | true | null> => {
    const v = r.pick([n.value ?? []].flat()) ?? '', at = io.now();
    let ok = false, err = '', made: Sim | null = null, placed = '';
    const calls: { cmd: string; args: object; sim?: string }[] = []; // logged: --replay repeats them (a made Sim's new name mapped)
    const step = async (cmd: string, args: object) => {
      const x = await call(cmd, args, /^buy_(open|close)$/.test(cmd) ? 200000 : 20000), c = { cmd, args, ...(x?.result?.sim && { sim: String(x.result.sim) }) }; // buy_open/close wait for the catalog (<= 180 s)
      calls.push(c);
      if (!x?.ok) throw new Error(`${cmd}: ${x?.error}`);
      return x.result;
    };
    try {
      if (n.kind === 'object') {
        const b = await buyInBuyMode(v, r, step);
        if (b?.id && b.where === 'placed') placed = String(b.id); // "inventory" = no room: nothing to target
      } else if (n.kind === 'season' || n.kind === 'moon') {
        const moon = n.kind === 'moon' ? (MOON[norm(v)] ?? Number(v)) : undefined;
        await step('test_env', { mode: 'fixed', season: n.kind === 'season' ? v.toLowerCase() : String(C.raw?.season ?? 'summer').toLowerCase(), ...(moon !== undefined && { moon_phase: moon }) });
      } else {
        if (spawns >= (cfg.maxSpawns ?? 6)) throw new Error('spawn budget spent');
        spawns++;
        // an occult role's Sim is made at the role's age (n.age), so make_occult judges the real age
        const species = n.kind === 'pet' ? v || 'Dog' : 'Human', age = n.kind === 'age' ? v : n.age ?? 'Adult';
        const gender = n.gender ?? r.pick(['Female', 'Male']);
        const m = await step('make_sim', { age, gender, species });
        if (n.kind === 'occult') await step('make_occult', { sim: m.sim, type: v });
        made = { id: String(m.id), name: String(m.sim), age, female: gender === 'Female', occult: n.kind === 'occult' ? [v] : [], active: !!m.active_household, ...(m.active_household && activeHousehold != null && { household: String(activeHousehold) }) };
      }
      ok = true;
    } catch (x: any) { err = String(x.message); }
    io.log({ kind: 'prepare', t: at, ms: io.now() - at, story: t.id, need: n.kind, value: v, ok, calls, ...(err && { error: err }), ...ctx });
    if (!ok) return null;
    C.have[n.kind].set(norm(v), (C.have[n.kind].get(norm(v)) ?? 0) + 1); // the world has it now
    if (made && n.kind !== 'pet') { assignPersona(made); driven.push(made); return made; }
    return placed || true;
  };

  // a step target: a string (with $ refs) or {find, value} resolved from the census, else list_objects
  const resolveTarget = async (tg: TStep['target'], r: ReturnType<typeof rng>, actors: Record<string, Sim>, sub: (x: any) => any, key: string, me?: Sim, t?: Template): Promise<{ ref?: string; lot?: string }> => {
    if (tg === undefined) return {};
    if (typeof tg === 'string') return { ref: sub(tg) };
    const vals = isAny(tg.value) ? [] : [tg.value!].flat().map(norm), match = (x: unknown) => !vals.length || vals.includes(norm(x));
    // never a Sim of another running story (its actors or its Sim targets)
    const busy = new Set([...Object.values(actors), ...driven.filter(s => s.busy), ...others.filter(s => s.busy)].map(s => s.id).concat([...claimed].filter(([, k]) => k !== key).map(([id]) => id)));
    if (tg.find === 'skillclass') { // the rabbit hole that teaches it: census skillclasses [{name, rabbithole}] or names, else any rabbit hole
      const c = r.pick((C.raw?.skillclasses ?? []).filter((c: any) => match(c?.name ?? c) && c?.rabbithole));
      const id = c?.rabbithole ?? r.pick((C.raw?.rabbitholes ?? []).filter((o: any) => o.id))?.id;
      if (id) return { ref: String(id) };
    } else if (tg.find === 'venue') { const l = r.pick((C.raw?.lots ?? []).filter((l: any) => l.venue && match(l.venue))); if (l) return { ref: `lot:${l.id}`, lot: `lot:${l.id}` }; }
    else if (['age', 'occult', 'pet'].includes(tg.find)) {
      const s = r.pick((C.raw?.sims ?? []).filter((s: any) => s.in_world !== false && !busy.has(String(s.id)) && (tg.find === 'age' ? match(s.age) : tg.find === 'pet' ? s.species && norm(s.species) !== 'human' && match(s.species) : [s.occult ?? []].flat().some(match))));
      if (s) { claimed.set(String(s.id), key); return { ref: String(s.name) }; }
    } else {
      const pool = tg.find === 'rabbithole' ? C.raw?.rabbitholes : tg.find === 'shop' ? C.raw?.shops : C.raw?.objects;
      const known = Array.isArray(pool) ? pool.filter((o: any) => match(o.type ?? o.kind)).flatMap((o: any) => o.id ? [o] : (o.ids ?? []).map((id: any) => ({ id })))
        : Object.entries(pool ?? {}).filter(([k]) => match(k)).flatMap(([, v]) => samplesOf(v));
      // the actor's own home first, then lots no household lives on: another household's bed, shower or fridge is not
      // usable by a visitor (story-smoke 2026-10-23: Take Shower / Sleep / Quick Meal on strangers' lots never matched)
      const home = me?.household != null ? homeLot?.[me.household] : undefined, homes = new Set(Object.values(homeLot ?? {}));
      const ids = known.filter((o: any) => o?.id), own = ids.filter((o: any) => home && String(o.lot) === home);
      const o = r.pick(own) ?? r.pick(ids.filter((o: any) => !homes.has(String(o.lot))));
      if (o) return { ref: String(o.id), ...(o.lot && { lot: `lot:${o.lot}` }) };
      const query = String(vals.length ? [tg.value!].flat()[0] : tg.find);
      const q = await call('list_objects', { query: query.replace(/\s+/g, ''), include_sims: false, ...(home && { lot: `lot:${home}` }) });
      const hit = r.pick(Array.isArray(q?.result) ? q.result.filter((x: any) => !x.in_use) : []);
      if (hit) return { ref: String(hit.id), ...(home && { lot: `lot:${home}` }) };
      // none placed (the census counts inventory copies too, e.g. skill books): the template's object prepare buys one now
      const nd = tg.find === 'object' ? t?.needs?.find(n => n.kind === 'object' && n.prepare && (!vals.length || [n.value ?? []].flat().some(v => vals.includes(norm(v))))) : undefined;
      if (nd && t && prepares < (cfg.maxPrepares ?? 20)) {
        prepares++;
        const id = await prepare(nd, r, t);
        if (typeof id === 'string') return { ref: id };
      }
    }
    throw new Error(`no ${tg.find}${tg.value ? ` ${[tg.value].flat().join('|')}` : ''} to target`);
  };

  const running: Record<string, number> = {}, lastEnd: Record<string, number> = {}, tasks: Promise<void>[] = [];
  let started = 0;
  const runOne = async (t: Template, lead: Sim, k: number) => {
    const r = rng(seed, k), actors: Record<string, Sim> = {}, mine: Sim[] = [];
    const release = () => { for (const s of mine) s.busy = undefined; for (const [id, x] of claimed) if (x === `${t.id}#${k}`) claimed.delete(id); running[t.id]--; lastEnd[t.id] = io.now(); };
    running[t.id] = (running[t.id] ?? 0) + 1; cov[t.id].runs++;
    const take = (role: string, s: Sim) => { actors[role] = s; s.busy = `${t.id}#${k}`; mine.push(s); };
    if (t.actors.length) take(t.actors[0].role, lead);
    try {
      for (const role of t.actors.slice(1)) {
        // family: only Sims of that role's household (a parent picks from the child's household, never a stranger)
        const fam = role.family ? actors[role.family] : undefined;
        // a role some step orders: household Sims only (R-HH-4), unless drive world or a stress template; else townies too
        const pool = world || t.realism === 'stress' || !t.steps.some(st => actorOf(st) === role.role) ? [...driven, ...others] : driven;
        const s = role.pick === 'new' ? null : r.pick(pool.filter(s => fits(s, role) && (!fam || (fam.household != null && s.household === fam.household))));
        if (s) { take(role.role, s); continue; }
        if (fam && role.pick !== 'new') throw new Error(`actor ${role.role}: none fits in ${role.family}'s household`);
        if (prepares >= (cfg.maxPrepares ?? 20)) throw new Error(`actor ${role.role}: none fits, prepare budget spent`);
        prepares++;
        const occ = role.occult == null ? null : r.pick([role.occult].flat());
        const age = r.pick(role.age ?? ['Adult']);
        const made = await prepare(occ ? { kind: 'occult', value: occ, age, gender: role.gender } : { kind: 'age', value: age, gender: role.gender }, r, t);
        if (!made || typeof made !== 'object') throw new Error(`actor ${role.role}: none fits and make_sim failed`);
        take(role.role, made);
      }
      for (const n of t.needs ?? []) {
        if (hasNeed(C, n)) continue;
        if (prepares >= (cfg.maxPrepares ?? 20)) throw new Error(`need ${n.kind}: prepare budget spent`);
        prepares++;
        if (!await prepare(n, r, t)) throw new Error(`need ${n.kind}: prepare failed`);
      }
    } catch (x: any) { skip(t, String(x.message).replace(/: .*/, ''), { run: k, error: x.message }); release(); return; }
    // the death role's Sim leaves the death guard for the rest of the run (its death may come after the last step)
    if (t.death && actors[t.death]) deathOwned.set(actors[t.death].id, `${t.id}#${k}`);
    const names = Object.fromEntries(Object.entries(actors).map(([k, s]) => [k, s.name]));
    let failed = false, unavailable = false, unlock: (() => void) | null = null;
    for (const [i, s] of t.steps.entries()) {
      if (io.halted() || io.now() - t0 >= capMs || stop) break;
      // work and school come first (docs/171 §16): an actor whose day plan reached its work or school block leaves the story
      // (story-days 2026-10-23: Morrigan Hemlock missed a night shift on a community lot inside social-visit-community)
      const away = Object.values(actors).find(a => /^day:(work|school)$/.test(a.blocked ?? ''));
      if (away) {
        cov[t.id].skipped['not available'] = (cov[t.id].skipped['not available'] ?? 0) + 1;
        io.log({ kind: 'skip', t: io.now(), story: t.id, why: 'not available', run: k, step: i, cmd: s.cmd, sim: away.name, error: `${away.blocked!.slice(4)} time: ${away.name} leaves the story`, ...ctx });
        unavailable = true; break;
      }
      if (!unlock && leavesLive(s)) unlock = await lockMode(k);
      const actor = s.actor ? actors[s.actor] : undefined;
      let target: { ref?: string; lot?: string } = {}, r2: any, cmd = s.cmd, ms = 0;
      const sub = (v: any): any => typeof v === 'string'
        ? v.replace(/\$actor\.([A-Za-z0-9_-]+)/g, (_, role) => names[role] ?? _).replace(/\$target\b/g, target.ref ?? '').replace(/\$lot\b/g, target.lot ?? actor?.lot ?? 'home')
        : Array.isArray(v) ? v.map(sub) : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, sub(x)])) : v;
      const exec = async () => {
        // c0 = when the logged call goes out (after list_interactions and any build-mode hold), so a replay keeps the offsets
        let c0 = -1, sent: any = null;
        const go = async (c: string, a: object, to?: number) => { while (gate && runNo.getStore() !== gateOwner) await gate; c0 = io.now(); return call(c, a, to); };
        try {
          target = await resolveTarget(s.target, r, actors, sub, `${t.id}#${k}`, actor, t);
          const args = { ...(actor && { sim: actor.name }), ...(target.ref && { target: target.ref }), ...sub(s.args ?? {}) };
          if (cmd === 'save_game') args.name = scratch; // never the loaded file, never a Baseline
          // travel takes the template's role (docs/179 R-TRAV-1/2: the YA+ traveller leads), not the agent's active Sim
          if ((cmd === 'travel_vacation' || cmd === 'travel_university') && actor && !args.sims) { args.sims = [actor.name]; delete args.sim; }
          // product "random": an affordable catalog product (the agent takes only exact keys)
          if (cmd === 'buy_object' && args.product === 'random') args.product = (await pickProduct('', r, async (c, a) => { const x = await call(c, a); if (!x?.ok) throw new Error(`${c}: ${x?.error}`); return x.result; })).product;
          if (s.cmd === 'interact' || s.cmd === 'any_interaction') {
            // interact: a random enabled entry whose label starts with the step's label (docs/171 §8: "Have" = any meal); the
            // exact label then goes to do_interaction, which takes an exact match before its substring match
            // (label = the whole entry or a path segment). A matching entry that is greyed out, or no enabled entry at all (at
            // work, in a rabbit hole, routing away) = the Sim is busy: a skip, not a label miss
            const l = await call('list_interactions', { ...args, include_disabled: true }), want = s.label?.toLowerCase();
            const all: any[] = (l?.result?.interactions ?? []).filter((x: any) => !x.pick_from), on = all.filter(x => !x.disabled_reason);
            const e = r.pick(on.filter(x => !want || labelHit(x.interaction, want)));
            const grey = want ? all.find(x => x.disabled_reason && labelHit(x.interaction, want)) : undefined;
            if (!e && want && l?.ok && on.length && !grey) cov[t.id].labelMiss[s.label!] = (cov[t.id].labelMiss[s.label!] ?? 0) + 1;
            r2 = e ? { ...await go((cmd = 'do_interaction'), (sent = { ...args, interaction: e.interaction })), picked: e.interaction }
              : { ok: false, error: !l?.ok ? l?.error : grey ? `busy: "${grey.interaction}" greyed out (${grey.disabled_reason})` : !on.length ? 'no enabled interactions (busy or away)'
                : want ? `no enabled interaction starting with "${s.label}"` : 'no enabled interaction' };
          } else r2 = await go(cmd, (sent = args), s.maxS ? Math.max(8000, s.maxS * 1000) : undefined);
        } catch (x: any) { r2 = { ok: false, error: x.message }; }
        if (c0 < 0) c0 = io.now(); // nothing sent (no target, no matching label)
        ms = io.now() - c0;
        if (!r2?.ok && UNAVAILABLE.test(String(r2?.error))) {
          r2 = { ok: false, unavailable: String(r2.error) };
          cov[t.id].skipped['not available'] = (cov[t.id].skipped['not available'] ?? 0) + 1; // one skip line each time (skip() logs a reason once)
          io.log({ kind: 'skip', t: c0, story: t.id, why: 'not available', run: k, step: i, cmd, sim: actor?.name ?? sent?.sim ?? null, error: r2.unavailable.slice(0, 300), ...ctx });
          // gone from every household (despawned townie, another world): never picked again
          if (/no sim named/.test(r2.unavailable)) for (const l of [driven, others]) for (const d of l.filter(d => r2.unavailable.includes(`'${d.name}'`))) l.splice(l.indexOf(d), 1);
          return r2;
        }
        // t = the step's start (epoch ms), ms = its call time (the wait after it is not counted); args = what was sent (--replay)
        io.log({ t: c0, seed, story: t.id, run: k, step: i, cmd, actor: actor?.name ?? null, simId: actor?.id ?? null, target: target.ref ?? null, lot: target.lot ?? actor?.lot ?? null,
          ok: !!r2?.ok, ms, args: sent, ...(r2?.picked && { label: r2.picked }), ...(!r2?.ok && { error: String(r2?.error ?? 'no answer').slice(0, 300) }), world: C.world, save: scratch });
        return r2;
      };
      // fixed waitS (default 10) or adaptive waitUntil, as repro steps (repro-loop.ts); the wait is the story's own, other stories run on
      // the next step answers the dialog this one opens (auto_dialog accept:true): keep the in-game autopilot off it during the wait
      if (t.steps[i + 1]?.cmd === 'auto_dialog' && t.steps[i + 1].args?.accept === true) await call('dialog_expect', { seconds: (s.waitS ?? 10) + 30, accept: true }); // the agent accepts it (413f985); the next step then finds it closed
      await runStep({ cmd: s.cmd, waitS: s.waitS ?? 10, waitUntil: s.waitUntil, minS: s.minS, maxS: s.maxS, note: `story ${t.id} k${k} step ${i}` } as LoopStep,
        { ag: call, step: exec, sleep: io.sleep, now: io.now, halted: () => io.halted() || io.now() - t0 >= capMs, log: l => print(l) }).catch(x => { r2 = { ok: false, error: x.message }; });
      // a step back to live mode hands the game back once the game is there (ensureLive), the rest of the story runs beside others
      if (unlock && (s.cmd === 'buy_close' || s.cmd === 'cas_close' || s.cmd === 'build_mode' && s.args?.on === false)) { await ensureLive(); unlock(); unlock = null; }
      if (r2?.unavailable) { if (s.optional) continue; unavailable = true; break; }
      if (r2?.ok) cov[t.id].ok++; else { cov[t.id].failed++; if (!s.optional) { failed = true; break; } }
    }
    if (unlock) { await ensureLive(); unlock(); }
    print(`story ${t.id} #${k} ${failed ? 'stopped at a failed step' : unavailable ? 'skipped: not available' : 'done'} (${Object.values(names).join(', ')})`);
    release();
  };

  const between = async () => {
    const lots = (C.raw?.lots ?? []).filter((l: any) => l.pos);
    if (lots.length && R.next() < (cfg.cameraP ?? 0.3)) {
      const l = R.pick(lots), p = l.pos, at = io.now();
      const args = { target: p, pos: { x: p.x + R.int(-30, 30), y: (p.y ?? 0) + 30, z: p.z + R.int(-30, 30) } };
      const r = await call('camera', args);
      io.log({ kind: 'between', t: at, ms: io.now() - at, cmd: 'camera', args, lot: `lot:${l.id}`, ok: !!r?.ok, ...ctx });
    }
    if (R.next() < (cfg.modeP ?? 0.05) && !stop) {
      // in and out within itself: build_mode only queues the switch, so wait for build mode before the hold and for live
      // mode after it (2026-10-23 crash: off was asked while the switch to build mode still ran, the game stayed there)
      const unlock = await lockMode(-1), hold = R.int(5, 15) * 1000;
      const at = io.now(), on = await io.ag('build_mode', { on: true });
      const inBuild = !!on?.ok && await waitState('BuildModeState', 30000);
      if (inBuild) await io.sleep(hold);
      const offAt = io.now(), off = await io.ag('build_mode', { on: false }), live = await ensureLive();
      io.log({ kind: 'between', t: at, ms: io.now() - at, hold: offAt - at, cmd: 'build_mode', args: { on: true }, ok: inBuild && live, ...(!(inBuild && live) && { error: String(on?.error ?? off?.error ?? (inBuild ? stop : 'never reached build mode')) }), ...ctx });
      unlock();
    }
  };

  // household rotation between sim days (docs/180 §5, days.rotate): no new story starts, running ones end on their own (never
  // cut: 180 s cap, else the next day), then the game's own switch (switch_household = Edit Town's Change Active Household,
  // refused where the player's button is) under the mode lock; the driven set becomes the new household, the rest partners
  let pauseNew = false;
  const rotate = async (day: number) => {
    const at = io.now(), cur = activeHousehold != null ? String(activeHousehold) : null;
    const adults = new Set((C.raw?.sims ?? []).filter((s: any) => s.household != null && s.in_world !== false && !s.species && ['YoungAdult', 'Adult', 'Elder'].includes(String(s.age))).map((s: any) => String(s.household)));
    const pick = R.pick(Object.keys(homeLot ?? {}).filter(h => h !== cur && adults.has(h)).sort());
    const done = (ok: boolean, extra: { error?: string; [k: string]: unknown }) => {
      io.log({ kind: 'rotate', t: at, ms: io.now() - at, day, from: cur, to: pick ?? null, ok, ...extra, ...ctx });
      if (!ok) print(`story: day ${day} household rotation not done (${extra.error})`); // the queue log showed only successes
      return ok;
    };
    if (!pick) return done(false, { error: 'no other household with a home lot and an adult in the world' });
    pauseNew = true;
    try {
      while (Object.values(running).some(n => n > 0)) { if (io.now() - at > 180000 || timeUp()) return done(false, { error: 'stories still running after 180 s: next day' }); await io.sleep(1000); }
      const unlock = await lockMode(-1);
      let r: any;
      try { r = await io.ag('switch_household', { household: pick }, 60000); if (r?.ok) await ensureLive(); } finally { unlock(); }
      if (!r?.ok) return done(false, { error: String(r?.error ?? 'no answer').slice(0, 300) });
      activeHousehold = pick;
      const now = (await listSims('household')).map((s: any) => asSim(s, true));
      const ids = new Set(now.map(s => s.id));
      driven.splice(0, driven.length, ...now); driven.forEach(assignPersona);
      if (!world) others.splice(0, others.length, ...(await townies()).filter((s: any) => !ids.has(String(s.id))).map((s: any) => asSim(s, false)));
      print(`story: day ${day} rotated to household ${r.result?.household ?? pick} (${now.map(s => s.name).join(', ')})`);
      return done(true, { household: r.result?.household ?? null, sims: now.map(s => s.name) });
    } finally { pauseNew = false; }
  };

  // day planner (docs/171 §12): runs beside the scheduler; stories only get Sims in free blocks (s.blocked)
  let ready = () => {};
  const firstTick = new Promise<void>(r => { ready = r; });
  // a pursuit object the home lot lacks: the same paid Buy-mode flow as an object prepare, as its own mode-lock owner (-2)
  const dayStep = async (cmd: string, args: object) => { const x = await call(cmd, args, /^buy_(open|close)$/.test(cmd) ? 200000 : 20000); if (!x?.ok) throw new Error(`${cmd}: ${x?.error}`); return x.result; };
  const buy = (query: string) => runNo.run(-2, () => buyInBuyMode(query, rng(seed, 99), dayStep));
  const days = cfg.days ? runDays({ call, sleep: io.sleep, now: io.now, timeUp, log: io.log, print }, { cfg: cfg.days === true ? {} : cfg.days, driven, deathOwned, seed, rng, ctx, ready: () => ready(), rotate, buy }) : null;
  if (days) await Promise.race([firstTick, days]);

  // scheduler: a free lead Sim, then a template weighted by its persona, the world and the categories
  const [g0, g1] = cfg.gapS ?? [3, 10];
  while (!timeUp() && started < maxStories) {
    const runningAll = Object.values(running).reduce((a, b) => a + b, 0);
    const free = driven.filter(s => !s.busy && !s.blocked && !claimed.has(s.id));
    if (pauseNew || runningAll >= (cfg.parallel ?? 4) || !free.length) { await io.sleep(1000); continue; }
    const lead = R.pick(free)!;
    const ready = eligible.filter(t => (running[t.id] ?? 0) < (t.maxConcurrent ?? 1) && io.now() - (lastEnd[t.id] ?? -Infinity) >= (t.cooldownMin ?? 0) * 60000 && (!t.actors.length || fits(lead, t.actors[0])));
    const t = R.weighted(ready, x => weightFor(x, lead));
    if (!t) { await io.sleep(2000); continue; }
    const k = ++started;
    print(`story ${t.id} #${k} lead ${lead.name} (${lead.age}${lead.persona ? `, ${lead.persona.id}` : ''})`);
    tasks.push(runNo.run(k, () => runOne(t, lead, k)));
    await between();
    await io.sleep(R.int(g0, g1) * 1000);
  }
  await Promise.all(tasks);
  if (days) await days;
  if (stop) throw new Error(stop);
  const rows = Object.entries(cov).sort(([a], [b]) => a.localeCompare(b));
  print(`coverage (seed ${seed}, ${started} stories): id runs ok failed skipped`);
  for (const [id, c] of rows) print(`  ${id.padEnd(28)} ${String(c.runs).padStart(3)} ${String(c.ok).padStart(4)} ${String(c.failed).padStart(4)}  ${Object.entries(c.skipped).map(([w, n]) => `${w}${n > 1 ? ` x${n}` : ''}`).join('; ')}${Object.keys(c.labelMiss).length ? `  label never matched: ${Object.entries(c.labelMiss).map(([l, n]) => `"${l}" x${n}`).join(', ')}` : ''}`);
  io.log({ kind: 'coverage', t: io.now(), stories: started, templates: cov, ...ctx });
  return cov;
}

// --replay (story-report.ts, piece D): the logged agent calls of one window of an earlier run, from s to s seconds after its
// first log line, at the same offsets. First every prepare logged before the window end (made Sims get new names: mapped in
// the args), then the step and between lines of the window. Story choices are in the logged args, so no draw is repeated;
// the game RNG is profile-world --seed's (seed_rng). Lines as runStories', plus replay: {of, t} (the original line's t).
export const PROFILE_DIR = resolve(import.meta.dirname, '..', '..', 'probe-out', 'profile');
export const readStoryLog = (tagOrFile: string) => readFileSync(existsSync(tagOrFile) ? tagOrFile : join(PROFILE_DIR, `${tagOrFile}-story.jsonl`), 'utf8')
  .split('\n').filter(l => l.trim()).map(l => JSON.parse(l));
export async function replayStories(io: StoryIO, cfg: StoryConfig & { replay: string; from?: number; to?: number }, lines = readStoryLog(cfg.replay)): Promise<{ steps: number; failed: number }> {
  if (!lines.length) throw new Error(`replay ${cfg.replay}: empty story log`);
  const print = io.print ?? (() => {}), T0 = lines[0].t, from = T0 + (cfg.from ?? 0) * 1000, to = T0 + (cfg.to ?? Infinity) * 1000;
  const { scratch } = await scratchGuard(io, cfg.seed ?? lines.find(l => l.seed !== undefined)?.seed ?? 1);
  const names = new Map<string, string>();
  const map = (v: any): any => typeof v === 'string' ? names.get(v) ?? v : Array.isArray(v) ? v.map(map) : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, map(x)])) : v;
  const send = async (cmd: string, args: any) => io.ag(cmd, cmd === 'save_game' ? { ...args, name: scratch } : map(args), 20000);
  for (const p of lines.filter(l => l.kind === 'prepare' && l.ok && l.t < to)) {
    const at = io.now(); let ok = true;
    for (const c of p.calls ?? []) { const r = await send(c.cmd, c.args); ok &&= !!r?.ok; if (c.sim && r?.result?.sim) names.set(c.sim, String(r.result.sim)); }
    io.log({ kind: 'prepare', t: at, ms: io.now() - at, story: p.story, need: p.need, value: p.value, ok, replay: { of: cfg.replay, t: p.t }, save: scratch });
  }
  // by start time: a line is written when its call ends, so the file order is not the start order
  const todo = lines.filter(l => l.t >= from && l.t < to && l.args && (!l.kind || l.kind === 'between')).sort((a, b) => a.t - b.t);
  const start = io.now();
  let steps = 0, failed = 0;
  for (const l of todo) {
    const due = start + (l.t - from);
    while (!io.halted() && io.now() < due) await io.sleep(Math.min(1000, due - io.now()));
    if (io.halted()) break;
    const at = io.now();
    const r = await send(l.cmd, l.args);
    if (l.cmd === 'build_mode' && l.kind === 'between') {
      await io.sleep(Math.max(0, (l.hold ?? l.ms) - (io.now() - at)));
      await send('build_mode', { on: false });
      // back in live mode before the next call (docs/171 §14); up to 60 s
      for (let n = 0; n < 60 && (await io.ag('game_state', {}, 10000))?.result?.sub_state !== 'LiveModeState'; n++) await io.sleep(1000);
    }
    steps++; if (!r?.ok) failed++;
    io.log({ ...l, t: at, ms: io.now() - at, ok: !!r?.ok, ...(!r?.ok ? { error: String(r?.error ?? 'no answer').slice(0, 300) } : { error: undefined }), actor: map(l.actor), target: map(l.target), save: scratch, replay: { of: cfg.replay, t: l.t } });
  }
  print(`replay ${cfg.replay} ${cfg.from ?? 0}-${cfg.to ?? 'end'} s: ${steps} calls, ${failed} failed`);
  return { steps, failed };
}

export const parseConfig = (spec: string): StoryConfig => {
  const c = spec.trim().startsWith('{') ? JSON.parse(spec) : readJson(spec);
  const pair = (v: any) => v === undefined || Array.isArray(v) && v.length === 2 && v.every(Number.isFinite) && v[0] <= v[1];
  if (c.seed !== undefined && !Number.isInteger(c.seed)) throw new Error('story config: seed must be an integer');
  if (!pair(c.driven) || !pair(c.gapS)) throw new Error('story config: driven / gapS must be [min, max]');
  for (const k of ['stories', 'minutes', 'parallel', 'maxPrepares', 'maxSpawns']) if (c[k] !== undefined && !(Number(c[k]) > 0)) throw new Error(`story config: ${k} must be > 0`);
  if (c.replay !== undefined && (typeof c.replay !== 'string' || !c.replay)) throw new Error('story config: replay must be a tag or a story.jsonl path');
  for (const k of ['from', 'to']) if (c[k] !== undefined && !(typeof c[k] === 'number' && c[k] >= 0)) throw new Error(`story config: ${k} must be seconds >= 0`);
  if (c.from !== undefined && c.to !== undefined && c.from >= c.to) throw new Error('story config: from must be < to');
  if (c.censusOnly !== undefined && typeof c.censusOnly !== 'boolean') throw new Error('story config: censusOnly must be true or false');
  if (c.travel !== undefined && typeof c.travel !== 'boolean') throw new Error('story config: travel must be true or false');
  if (c.drive !== undefined && c.drive !== 'household' && c.drive !== 'world') throw new Error('story config: drive must be household or world');
  // household: save (default) plays the save's active household; generated | <id> = a docs/181 household made in CAS first
  if (c.household !== undefined && (typeof c.household !== 'string' || !c.household)) throw new Error('story config: household must be save, generated or a stories/households id');
  // householdFunds: the familyfunds cheat on the generated household (docs/181 §3.1): only with a generated household and realism cheat
  if (c.householdLot !== undefined && (!/^\d+$/.test(String(c.householdLot)) || !c.household || c.household === 'save')) throw new Error('story config: householdLot must be a lot id (list_lots) with a generated household');
  if (c.householdFunds !== undefined) {
    if (!(Number.isInteger(c.householdFunds) && c.householdFunds > 0)) throw new Error('story config: householdFunds must be an integer > 0');
    if (!c.household || c.household === 'save') throw new Error('story config: householdFunds needs household generated or a stories/households id');
    if (!(c.realism ?? ['natural', 'cheat']).includes('cheat')) throw new Error('story config: householdFunds is the familyfunds cheat: realism must include cheat');
  }
  if (c.days !== undefined && typeof c.days !== 'boolean' && !(c.days && typeof c.days === 'object' && !Array.isArray(c.days)
    && ['tickS', 'workPursuits', 'freePursuits'].every(k => c.days[k] === undefined || Number(c.days[k]) >= 0) && ['nightSkip', 'town'].every(k => c.days[k] === undefined || typeof c.days[k] === 'boolean')
    && (c.days.rotate === undefined || Number.isInteger(c.days.rotate) && c.days.rotate >= 0)))
    throw new Error('story config: days must be true or {tickS, nightSkip, workPursuits, freePursuits, town, rotate}');
  return c;
};

// profile-world --story hook: config and content checked before the game is touched; run() drives the window
export function loadStory(spec: string, o: { seed?: string; envFree?: boolean }) {
  const cfg = parseConfig(spec);
  cfg.seed ??= o.seed !== undefined ? Number(o.seed) : 1;
  // replay: the source log read now (the run's own <tag>-story.jsonl is rewritten at the window start, maybe the same file)
  const replayLines = cfg.replay ? readStoryLog(cfg.replay) : null;
  const { content, errors } = replayLines ? { content: null, errors: [] } : loadContent(cfg.content ? resolve(cfg.content) : STORIES_DIR);
  if (errors.length) throw new Error(`story content errors:\n  ${errors.join('\n  ')}`);
  if (content && !content.templates.length) throw new Error('story: no templates in tools/harness/stories/templates');
  return {
    cfg,
    run: async (io: { ag: StoryIO['ag']; sleep: StoryIO['sleep']; halted: () => boolean; pre: string }) => {
      const f = `${io.pre}-story.jsonl`;
      writeFileSync(f, '');
      const stamp = () => new Date().toISOString().slice(11, 19);
      const sio: StoryIO = { ...io, now: Date.now, envFree: o.envFree, log: l => appendFileSync(f, JSON.stringify(l) + '\n'), print: l => console.log(`${stamp()} ${l}`) };
      try {
        if (replayLines) await replayStories(sio, cfg as any, replayLines);
        else {
          // throws "household creation failed: ..." (never the save's household silently): rethrown below, the job stops
          if (cfg.household && cfg.household !== 'save') cfg.personaOf = await createHousehold(sio, cfg.household, cfg.seed!, cfg.householdFunds, cfg.householdLot);
          await runStories(sio, cfg, content!);
        }
      } catch (x: any) {
        appendFileSync(f, JSON.stringify({ kind: 'skip', t: Date.now(), story: '*', why: 'run', error: x.message }) + '\n');
        if (/^household creation failed/.test(x.message)) throw x; // profile-world: FAIL + exit 1
        console.log(`${stamp()} FAIL: ${x.message}`);
      }
    },
  };
}

// ---- fake game + virtual clock (dry run and self-check)
// mode: the game's sub state; build/buy/CAS switches land 3 s after the call (the game queues them), stuckBuild = never leaves build
export function fakeGame(raw: any, o: { save?: string; failSave?: boolean; fail?: string[]; errors?: Record<string, string>; stuckBuild?: boolean } = {}) {
  let save = o.save ?? 'Baseline-SV', t = 0, seq = 0, made = 0, mode = 'LiveModeState', snaps = 0;
  const q: { at: number; seq: number; r: () => void }[] = [], calls: { cmd: string; args?: any; t: number; mode: string }[] = [];
  const switchTo = (m: string) => { if (!(o.stuckBuild && mode === 'BuildModeState')) q.push({ at: t + 3000, seq: seq++, r: () => { mode = m; } }); };
  const sims = (fromAgent(raw).sims ?? []).map((s: any) => ({ ...s }));
  const ag = async (cmd: string, args?: any): Promise<any> => {
    calls.push({ cmd, args, t, mode });
    if (o.fail?.includes(cmd)) return { ok: false, error: `${cmd} refused (fixture)` };
    if (o.errors?.[cmd]) return { ok: false, error: o.errors[cmd].replace('$sim', String(args?.sim)) }; // the game's own error text
    const sum = (s: any) => ({ id: s.id, name: s.name, age: s.age, in_world: s.in_world !== false, ...(s.species && { species: s.species }) });
    switch (cmd) {
      case 'game_state': return { ok: true, result: { sub_state: mode, save_name: save } };
      case 'build_mode': switchTo(args?.on === false ? 'LiveModeState' : 'BuildModeState'); return { ok: true, result: {} };
      case 'buy_open': switchTo('BuyModeState'); return { ok: true, result: {} };
      case 'cas_open': switchTo('CASFullModeState'); return { ok: true, result: {} };
      case 'buy_close': case 'cas_close': case 'enter_live_mode': switchTo('LiveModeState'); return { ok: true, result: {} };
      case 'save_game': if (o.failSave) return { ok: false, error: 'the game cannot save right now' }; save = args.name; return { ok: true, result: { saving: save } };
      case 'world_census': return { ok: true, result: raw };
      case 'list_sims': return { ok: true, result: sims.filter((s: any) => args?.scope === 'world' || s.active).map(sum) };
      case 'spawn_sims': { let n = args.count; for (const s of sims) if (n > 0 && s.in_world === false) { s.in_world = true; n--; } return { ok: true, result: {} }; }
      case 'make_sim': { const s = { id: `M${++made}`, name: `Made ${made}`, age: args.age, active: true, household: sims.find((x: any) => x.active)?.household, ...(args.species !== 'Human' && { species: args.species }) }; sims.push(s); return { ok: true, result: { sim: s.name, id: s.id, active_household: true } }; }
      case 'list_interactions': return { ok: true, result: { interactions: [{ interaction: 'Chat' }, { interaction: 'Read' }, { interaction: 'Greyed', disabled_reason: 'x' }, { interaction: 'Watch TV' }] } };
      case 'list_objects': return { ok: true, result: [{ id: `OBJ-${norm(args.query)}`, name: args.query }] };
      case 'catalog_search': return { ok: true, result: [{ product: `0x${norm(args.query)}`, name: args.query }] };
      case 'household_funds': return { ok: true, result: { household: 'Goth', funds: 1000 } };
      // off-camera life (docs/180): from the 2nd snapshot on a Landgraab baby is born, a Hart Sim gets a job, Landgraab 0 and
      // Hart 0 marry, Alto 0 leaves town; switch_household moves `active` to that household's Sims
      case 'town_snapshot': {
        const n = ++snaps, act = sims.find((s: any) => s.active)?.household ?? null, hn = (h: string) => (raw.households ?? []).find((x: any) => x.id === h)?.name;
        const list = sims.filter((s: any) => !(n > 1 && s.name === 'Townie 4')).map((s: any) => ({ id: s.id, name: s.name, age: s.age, household: s.household ?? null, household_name: hn(s.household),
          lot: (raw.households ?? []).find((x: any) => x.id === s.household)?.lot ?? null, ...(n > 1 && s.name === 'Townie 8' && { job: 'Business', level: 1 }),
          ...(n > 1 && ['Townie 0', 'Townie 5'].includes(s.name) && { partner: sims.find((x: any) => x.name === (s.name === 'Townie 0' ? 'Townie 5' : 'Townie 0'))?.id, married: true }) }));
        if (n > 1) list.push({ id: 'B1', name: 'Baby Landgraab', age: 'Baby', household: '9101', household_name: 'Landgraab', lot: '105' });
        return { ok: true, result: { day_number: n, active_household: act, story_progression: 'Enabled', sims: list } };
      }
      case 'switch_household': {
        if (!sims.some((s: any) => s.household === args.household)) return { ok: false, error: `no household '${args.household}'` };
        for (const s of sims) s.active = s.household === args.household;
        return { ok: true, result: { household: (raw.households ?? []).find((x: any) => x.id === args.household)?.name ?? args.household, id: args.household } };
      }
      default: return { ok: true, result: {} };
    }
  };
  const sleep = (ms: number) => new Promise<void>(r => { q.push({ at: t + ms, seq: seq++, r }); });
  // runs main on the virtual clock: whenever every task waits, the earliest sleeper wakes
  const drive = async <T>(main: Promise<T>): Promise<T> => {
    let done = false;
    main.then(() => { done = true; }, () => { done = true; });
    for (;;) {
      for (let i = 0; i < 3; i++) await new Promise(r => setImmediate(r));
      if (done) return main;
      if (!q.length) throw new Error('fake game: no task runnable');
      q.sort((a, b) => a.at - b.at || a.seq - b.seq);
      const e = q.shift()!; t = Math.max(t, e.at); e.r();
    }
  };
  return { ag, sleep, now: () => t, drive, calls, save: () => save };
}

export const FIXTURE_TEMPLATES: Template[] = [
  { id: 'daily-cook', category: 'normal', actors: [{ role: 'cook', age: ['YoungAdult', 'Adult', 'Elder'] }], needs: [{ kind: 'object', value: 'stove' }],
    steps: [{ cmd: 'interact', actor: 'cook', target: { find: 'object', value: 'stove' }, label: 'Prepare Food', waitS: 30 }], cooldownMin: 2 },
  { id: 'skill-read', category: 'normal', actors: [{ role: 'reader' }], needs: [{ kind: 'object', value: 'book' }],
    steps: [{ cmd: 'any_interaction', actor: 'reader', target: { find: 'object', value: 'book' }, waitS: 20 }] },
  { id: 'social-chat', category: 'normal', weight: 2, actors: [{ role: 'a', age: ['Teen', 'YoungAdult', 'Adult', 'Elder'] }, { role: 'b', age: ['Teen', 'YoungAdult', 'Adult', 'Elder'] }],
    steps: [{ cmd: 'interact', actor: 'a', target: '$actor.b', label: 'Chat', waitS: 15 }, { cmd: 'any_interaction', actor: 'b', target: '$actor.a', waitS: 15, optional: true }], maxConcurrent: 2 },
  { id: 'world-park', category: 'world', actors: [{ role: 'v' }], needs: [{ kind: 'venue', value: 'BigPark' }],
    steps: [{ cmd: 'go_here', actor: 'v', target: { find: 'venue', value: 'BigPark' }, waitS: 40 }, { cmd: 'save_game', args: { name: 'Baseline-SV' }, waitS: 5 }] },
  { id: 'world-spa', category: 'world', actors: [{ role: 'v' }], needs: [{ kind: 'rabbithole', value: 'Spa' }], steps: [{ cmd: 'go_here', actor: 'v', waitS: 5 }] },
  { id: 'occult-werewolf', category: 'occult', actors: [{ role: 'w', age: ['Adult'] }, { role: 'wolf', occult: 'Werewolf', pick: 'driven' }], needs: [{ kind: 'object', value: 'pool', prepare: true }],
    steps: [{ cmd: 'interact', actor: 'wolf', target: '$actor.w', label: 'Chat', waitS: 10 }], cooldownMin: 60 },
  { id: 'chaos-winter', category: 'chaos', actors: [{ role: 'x' }], needs: [{ kind: 'season', value: 'Winter', prepare: true }], steps: [{ cmd: 'start_fire', actor: 'x', waitS: 5 }] },
];

if (import.meta.url === `file:///${process.argv[1]?.replace(/\\/g, '/').replace(/^\//, '')}`) {
  const { parseArgs } = await import('node:util');
  const { values } = parseArgs({ options: { check: { type: 'boolean' }, 'dry-run': { type: 'boolean' }, seed: { type: 'string' }, config: { type: 'string' }, census: { type: 'string' }, content: { type: 'string' }, minutes: { type: 'string' }, 'env-free': { type: 'boolean' }, replay: { type: 'string' }, from: { type: 'string' }, to: { type: 'string' } } });
  const dir = values.content ? resolve(values.content) : STORIES_DIR;
  // the plan of one seed on the fake game: log lines as plan lines (virtual mm:ss)
  const dry = async (cfg: StoryConfig, content: Content, raw: any, o: { failSave?: boolean; save?: string; envFree?: boolean; quiet?: boolean; errors?: Record<string, string>; stuckBuild?: boolean } = {}) => {
    const g0 = fakeGame(raw, o), g = cfg.days ? withDays(g0, fromAgent(raw).sims) as typeof g0 : g0, lines: any[] = [], out: string[] = [];
    const mmss = (t: number) => `${String(Math.floor(t / 60000)).padStart(2, '0')}:${String(Math.floor(t / 1000) % 60).padStart(2, '0')}`;
    const io: StoryIO = { ag: g.ag, sleep: g.sleep, now: g.now, halted: () => false, envFree: o.envFree, census: normalize(raw, 'fixture'),
      log: l => { lines.push(l); const x: any = l; out.push(`${mmss(x.t)} ${x.kind ?? 'step'} ${x.story ?? ''}${x.sim ? `${x.sim} ${x.danger ?? x.type ?? x.block ?? x.skill ?? x.motive ?? ''}` : ''}${x.run ? ` #${x.run}` : ''}${x.step !== undefined ? ` s${x.step} ${x.cmd} ${x.actor ?? ''} -> ${x.target ?? '-'}${x.label ? ` "${x.label}"` : ''}` : ''}${x.kind === 'prepare' ? ` ${x.need ?? x.what} ${x.value ?? x.count ?? ''}` : ''}${x.kind === 'between' ? `${x.cmd} ${x.lot ?? ''}` : ''}${x.kind === 'scratch' ? `${x.from} -> ${x.save}` : ''}${x.why ? ` (${x.why})` : ''}${x.ok === false ? ` FAIL ${x.error ?? ''}` : ''}`); },
      print: l => { if (!o.quiet) out.push(`         ${l}`); } };
    const cov = await g.drive(runStories(io, cfg, content));
    return { cov, lines, out, calls: g.calls, save: g.save() };
  };
  if (values.replay) {
    // standalone replay on the running game (no profiler: a measured replay goes through profile-world --story '{"replay":...}')
    const cfg = parseConfig(JSON.stringify({ replay: values.replay, ...(values.seed && { seed: Number(values.seed) }), ...(values.from && { from: Number(values.from) }), ...(values.to && { to: Number(values.to) }) })) as any;
    const ag = async (cmd: string, args?: object, to = 8000) => {
      const q = new URLSearchParams({ cmd, timeout: String(to) }); if (args) q.set('args', JSON.stringify(args));
      try { return await (await fetch(`http://127.0.0.1:47891/agent?${q}`, { signal: AbortSignal.timeout(to + 5000) })).json(); } catch (e) { return { ok: false, error: String(e) }; }
    };
    const out = join(PROFILE_DIR, `${basename(values.replay).replace(/-story\.jsonl$/, '')}-replay-story.jsonl`), lines = readStoryLog(values.replay);
    writeFileSync(out, '');
    await replayStories({ ag, sleep: ms => new Promise(r => setTimeout(r, ms)), now: Date.now, halted: () => false, log: l => appendFileSync(out, JSON.stringify(l) + '\n'), print: l => console.log(l) }, cfg, lines);
    console.log(`log: ${out}`);
  } else if (values.check) {
    const { content, errors } = loadContent(dir);
    console.log(`${content.templates.length} templates, ${content.personas.length} personas, ${Object.keys(content.worlds).length} world files in ${dir}`);
    for (const e of errors) console.log(`  ERROR ${e}`);
    process.exit(errors.length ? 1 : 0);
  } else if (values['dry-run']) {
    const cfg: StoryConfig = { ...(values.config ? parseConfig(values.config) : {}), ...(values.seed && { seed: Number(values.seed) }) };
    cfg.minutes ??= values.minutes ? Number(values.minutes) : 30;
    const { content, errors } = loadContent(cfg.content ? resolve(cfg.content) : dir);
    if (errors.length) { console.log(errors.map(e => `ERROR ${e}`).join('\n')); process.exit(1); }
    if (!content.templates.length) { console.log('(no templates in the content dir: fixture templates)'); content.templates = FIXTURE_TEMPLATES; }
    const raw = values.census ? readJson(values.census) : FIXTURE;
    const r = await dry(cfg, content, raw, { envFree: values['env-free'] });
    console.log(r.out.join('\n'));
  } else {
    const content: Content = { templates: FIXTURE_TEMPLATES, personas: [{ id: 'kid', household: { ages: ['Child', 'Teen'] }, templateBias: { 'skill-': 3 } }, { id: 'grown', household: { ages: ['YoungAdult', 'Adult', 'Elder'] }, categoryBias: { world: 2 } }], categories: { normal: 0.5, world: 0.2, occult: 0.2, chaos: 0.1, lifestage: 0.1 }, worlds: { sunsetvalley: { templateBias: { 'daily-': 2 } } } };
    assert.deepEqual(checkTemplates(FIXTURE_TEMPLATES), []);
    const bad = checkTemplates([{ id: 'Bad Id' }, { id: 'x', category: 'odd', actors: [{ role: 'a', age: ['Old'] }], needs: [{ kind: 'planet' }], steps: [{ cmd: 'interact', actor: 'b' }, { cmd: 'load_save' }, { cmd: 'go', args: { s: '$actor.zz' }, waitUntil: { quiet: 1 } }], maxConcurrent: 0 }]);
    for (const m of ['kebab-case', 'category', 'age must', 'need kind planet', 'actor b is not a role', 'interact needs a label', 'not allowed', '$actor.zz', 'maxS', 'maxConcurrent']) assert.ok(bad.some(e => e.includes(m)), `schema: ${m} in ${bad.join(' / ')}`);
    assert.equal(prefixBias({ 'daily-': 1.5, 'daily-work': 3 }, 'daily-work-office'), 3);
    assert.equal(prefixBias({ 'daily-': 1.5 }, 'skill-x'), 1);
    const cfg: StoryConfig = { seed: 7, minutes: 20, stories: 25 };
    const a = await dry(cfg, content, FIXTURE, { quiet: true }), b = await dry(cfg, content, FIXTURE, { quiet: true }), c = await dry({ ...cfg, seed: 8 }, content, FIXTURE, { quiet: true });
    assert.deepEqual(a.out, b.out, 'same seed, same plan');
    assert.notDeepEqual(a.out, c.out, 'another seed, another plan');
    // scratch copy first, then the Baseline name never written; a template's save_game gets the scratch name
    assert.equal(a.calls[0].cmd, 'game_state'); assert.deepEqual(a.calls[1], { cmd: 'save_game', args: { name: 'Story-Baseline-SV-s7' }, t: 0, mode: 'LiveModeState' });
    assert.ok(a.calls.filter(x => x.cmd === 'save_game').every(x => x.args.name === 'Story-Baseline-SV-s7'));
    const steps = a.lines.filter(l => !l.kind), kinds = (k: string) => a.lines.filter(l => l.kind === k);
    assert.equal(kinds('census').length, 1);
    assert.deepEqual(kinds('skip').filter(l => /^missing/.test(l.why)).map(l => l.story).sort(), ['chaos-winter', 'world-spa'], 'needs: spa missing, winter only with envFree');
    assert.ok(kinds('prepare').some(l => l.need === 'object' && l.value === 'pool' && l.ok), 'pool bought');
    assert.ok(a.calls.some(x => x.cmd === 'make_occult' && x.args.type === 'Werewolf') || a.cov['occult-werewolf'].runs === 0, 'werewolf actor made');
    for (const l of steps) for (const k of ['t', 'seed', 'story', 'run', 'step', 'cmd', 'actor', 'simId', 'target', 'lot', 'ok', 'ms', 'world', 'save']) assert.ok(k in l, `step line field ${k}`);
    assert.ok(steps.some(l => l.cmd === 'do_interaction' && ['Chat', 'Read', 'Watch TV'].includes(l.label)) , 'any_interaction picked an enabled entry');
    assert.ok(!steps.some(l => l.label === 'Greyed'));
    const runs = new Set(steps.map(l => l.run));
    assert.ok(runs.size <= 25 && a.lines.at(-1).kind === 'coverage' && a.lines.at(-1).stories === 25, 'stopped at N stories');
    // no Sim in two stories at once; maxConcurrent and cooldown held (from the step spans: first to last step per run)
    const span: Record<number, { id: string; a: number; b: number; sims: Set<string> }> = {};
    for (const l of steps) { const s = span[l.run] ??= { id: l.story, a: l.t, b: l.t, sims: new Set() }; s.b = l.t; if (l.simId) s.sims.add(l.simId); }
    const sp = Object.values(span);
    for (const x of sp) for (const y of sp) if (x !== y && x.a < y.b && y.a < x.b) {
      assert.ok(![...x.sims].some(s => y.sims.has(s)), `Sim in two stories: ${x.id} / ${y.id}`);
      if (x.id === y.id) assert.ok(x.id === 'social-chat', `${x.id} ran twice at once (maxConcurrent 1)`);
    }
    const cook = sp.filter(x => x.id === 'daily-cook').sort((p, q) => p.a - q.a);
    for (let i = 1; i < cook.length; i++) assert.ok(cook[i].a - cook[i - 1].b >= 2 * 60000 + 30000 - 1, 'cooldown 2 min after the 30 s wait');
    // envFree: winter is prepared through test_env fixed
    const e = await dry({ ...cfg, only: ['chaos-'] }, content, FIXTURE, { envFree: true, quiet: true });
    assert.ok(e.calls.some(x => x.cmd === 'test_env' && x.args.season === 'winter'));
    // time cap: 3 virtual minutes
    const tc = await dry({ seed: 7, minutes: 3 }, content, FIXTURE, { quiet: true });
    assert.ok(tc.lines.filter(l => !l.kind).every(l => l.t < 3 * 60000) && tc.lines.at(-1).kind === 'coverage');
    // refused: the scratch copy fails on a Baseline save
    await assert.rejects(dry(cfg, content, FIXTURE, { failSave: true, quiet: true }), /refusing to change a Baseline save/);
    // already a scratch save: no second copy
    const s2 = await dry({ ...cfg, stories: 2 }, content, FIXTURE, { save: 'Story-Baseline-SV-s7', quiet: true });
    assert.equal(s2.calls.filter(x => x.cmd === 'save_game' && x.t === 0).length, 0);
    // interact labels match as a prefix; a label no entry starts with is counted for the coverage table
    assert.ok(steps.filter(l => l.story === 'daily-cook').every(l => !l.ok && /starting with "Prepare Food"/.test(l.error)) && a.cov['daily-cook'].labelMiss['Prepare Food'] > 0);
    assert.ok(steps.filter(l => l.story === 'social-chat' && l.step === 0).every(l => l.ok && l.label === 'Chat' && l.args.interaction === 'Chat'));
    // first live run (docs/171 §13): path segments match, unavailable Sims skip, travel templates skip unless travel: true
    assert.ok(labelHit('Friendly... / Chat', 'chat') && labelHit('Quick Meal... / Cereal', 'quick meal') && labelHit('Order Drink... / Simoleon Sunrise', 'order drink... / simoleon') && !labelHit('Watch TV', 'chat'));
    for (const m of ["no sim named 'Marie Desmarais'", 'Wogan Hemlock has no motive Hunger', 'X is not currently in the world (not instantiated)', 'busy: "Take Shower" greyed out (x)', 'no enabled interactions (busy or away)']) assert.ok(UNAVAILABLE.test(m), m);
    assert.ok(!UNAVAILABLE.test('no enabled interaction starting with "Chat"') && !UNAVAILABLE.test('no object skill book to target'));
    const travelT: Template = { id: 'travel-x', category: 'normal', actors: [], steps: [{ cmd: 'travel_university', waitS: 5 }] };
    const tv = await dry({ seed: 1, stories: 1, minutes: 1 }, { ...content, templates: [travelT] }, FIXTURE, { quiet: true });
    assert.ok(tv.lines.some(l => l.kind === 'skip' && l.story === 'travel-x' && /travel/.test(l.why)) && !tv.calls.some(c => c.cmd === 'travel_university'), 'travel skipped');
    // a Sim gone from every household: one skip line, the story ends unfailed, the Sim leaves the driven set
    const goneT: Template = { id: 'gone-x', category: 'normal', actors: [{ role: 'a' }], steps: [{ cmd: 'set_motive', actor: 'a', args: { motive: 'Hunger', value: -50 }, waitS: 5 }] };
    const gone = await dry({ seed: 1, stories: 2, minutes: 2 }, { ...content, templates: [goneT] }, FIXTURE, { quiet: true, errors: { set_motive: "no sim named '$sim'" } });
    assert.ok(gone.lines.some(l => l.kind === 'skip' && l.why === 'not available') && gone.cov['gone-x'].failed === 0 && !gone.lines.some(l => !l.kind && l.cmd === 'set_motive'), 'not available = skip');
    // game modes (docs/171 §14): the between build switch and mode stories own the game until it is live again; no Sim or
    // object change lands outside live mode; a game stuck in build mode stops the run
    const LIVE_ONLY = new Set(['make_sim', 'make_occult', 'pregnancy_advance', 'start_fire', 'buy_object', 'sell_object', 'set_motive', 'do_interaction', 'go_here', 'spawn_sims']);
    const modeT: Template = { id: 'buy-x', category: 'normal', actors: [{ role: 'o' }], steps: [{ cmd: 'buy_open', waitS: 5 }, { cmd: 'buy_close', waitS: 5 }, { cmd: 'buy_object', args: { product: 'random' } }] };
    const md = await dry({ seed: 1, stories: 12, modeP: 1, parallel: 4 }, { ...content, templates: [...content.templates, modeT] }, FIXTURE, { quiet: true });
    // buy_object / sell_object also in Buy mode (the player's buy flow, docs/179 R-MODE-3)
    const offLive = md.calls.filter(c => c.mode !== 'LiveModeState' && LIVE_ONLY.has(c.cmd) && !(c.mode === 'BuyModeState' && /^(buy|sell)_object$/.test(c.cmd)));
    assert.deepEqual(offLive.map(c => `${c.cmd}@${c.t} in ${c.mode}`), [], 'no Sim/object change outside live mode');
    // object prepares (docs/171 §16): Buy mode, household pays (no free), back in live mode after
    const buys = a.calls.filter(c => c.cmd === 'buy_object');
    assert.ok(buys.length && buys.every(c => c.mode === 'BuyModeState' && !c.args.free), `prepare buys in Buy mode, paid: ${buys.map(c => `${c.mode}/${c.args.free}`).join(' ')}`);
    assert.ok(a.calls.some(c => c.cmd === 'catalog_search' && c.args.max_price === 1000), 'catalog capped at the household funds');
    // household mode (docs/179 R-HH-4): only household Sims are ordered; townies only as partners or targets
    const hhNames = new Set(fromAgent(FIXTURE).sims.filter((s: any) => s.active).map((s: any) => s.name).concat(a.lines.filter(l => l.kind === 'prepare').flatMap(l => l.calls).filter((c: any) => c.sim).map((c: any) => c.sim)));
    assert.deepEqual(steps.filter(l => l.actor && !hhNames.has(l.actor)).map(l => `${l.story}: ${l.actor}`), [], 'only household Sims act');
    const chatT: Template = { id: 'chat-y', category: 'normal', actors: [{ role: 'a', age: ['Adult'] }, { role: 'b', age: ['YoungAdult', 'Adult', 'Elder'] }], steps: [{ cmd: 'interact', actor: 'a', target: '$actor.b', label: 'Chat', waitS: 15 }] };
    const ch = await dry({ seed: 4, stories: 8 }, { ...content, templates: [chatT] }, FIXTURE, { quiet: true });
    assert.ok(ch.lines.some(l => !l.kind && /^Townie/.test(l.target ?? '')) && ch.lines.filter(l => !l.kind).every(l => hhNames.has(l.actor)), 'townies take part as partners, household Sims act');
    const wd = await dry({ ...cfg, drive: 'world' }, content, FIXTURE, { quiet: true });
    assert.ok(wd.lines.filter(l => !l.kind).some(l => /^Townie/.test(l.actor ?? '')), 'drive world (stress): townies act');
    // travel steps send the role's Sim, not the active Sim
    const trT: Template = { id: 'travel-y', category: 'normal', actors: [{ role: 'tourist', age: ['Adult'] }], steps: [{ cmd: 'travel_vacation', actor: 'tourist', args: { world: 'france' }, waitS: 5 }] };
    const trv = await dry({ seed: 1, stories: 1, minutes: 1, travel: true }, { ...content, templates: [trT] }, FIXTURE, { quiet: true });
    const tc0 = trv.calls.find(c => c.cmd === 'travel_vacation');
    assert.ok(tc0 && Array.isArray(tc0.args.sims) && tc0.args.sims.length === 1 && hhNames.has(tc0.args.sims[0]) && !('sim' in tc0.args), `travel sims: ${JSON.stringify(tc0?.args)}`);
    const bw = md.lines.filter(l => l.kind === 'between' && l.cmd === 'build_mode');
    assert.ok(bw.length && bw.every(l => l.ok) && md.calls.some(c => c.mode === 'BuildModeState') && md.calls.some(c => c.cmd === 'buy_object' && c.mode === 'LiveModeState'), 'between in and out, buy after close');
    await assert.rejects(dry({ seed: 1, stories: 4, modeP: 1 }, content, FIXTURE, { quiet: true, stuckBuild: true }), /not back in live mode/);
    // replay: the window 60-300 s of run a, same calls in the same order at the same offsets, made Sims renamed
    const from = 60, to = 300, T0 = a.lines[0].t;
    const want = a.lines.filter(l => l.args && (!l.kind || l.kind === 'between') && l.t >= T0 + from * 1000 && l.t < T0 + to * 1000).sort((p, q) => p.t - q.t);
    const g = fakeGame(FIXTURE), rl: any[] = [];
    const rr = await g.drive(replayStories({ ag: g.ag, sleep: g.sleep, now: g.now, halted: () => false, log: l => rl.push(l) }, { seed: 7, replay: 'fixture', from, to }, a.lines));
    const sent = rl.filter(l => l.replay && !l.kind || l.kind === 'between');
    assert.equal(rr.steps, want.length);
    assert.deepEqual(sent.map(l => l.cmd), want.map(l => l.cmd));
    assert.deepEqual(sent.map(l => l.t - sent[0].t), want.map(l => l.t - want[0].t), 'same offsets');
    const made = a.lines.filter(l => l.kind === 'prepare' && l.t < T0 + to * 1000).flatMap(l => l.calls).filter((c: any) => c.cmd === 'make_sim').length;
    assert.equal(g.calls.filter(c => c.cmd === 'make_sim').length, made, 'prepares before the window end replayed');
    // a template without actors (a world story) runs without a lead
    const w = await dry({ seed: 3, stories: 3, only: ['cam-'] }, { ...content, templates: [{ id: 'cam-far', category: 'normal', actors: [], steps: [{ cmd: 'camera_jumps', waitS: 5 }] }] }, FIXTURE, { quiet: true });
    assert.equal(w.lines.filter(l => !l.kind && l.cmd === 'camera_jumps' && l.actor === null).length, 3);
    // the repo's content, when present, passes the schema
    const repo = loadContent();
    assert.deepEqual(repo.errors, [], repo.errors.join('\n'));
    assert.deepEqual(parseConfig('{"seed":3,"driven":[8,12]}'), { seed: 3, driven: [8, 12] });
    assert.throws(() => parseConfig('{"driven":[12,8]}'), /driven/);
    // householdFunds (familyfunds cheat, docs/181 §3.1): a generated household and realism cheat only
    assert.equal(parseConfig('{"household":"generated","householdFunds":30000}').householdFunds, 30000);
    assert.throws(() => parseConfig('{"household":"generated","householdFunds":30000,"realism":["natural"]}'), /realism must include cheat/);
    assert.throws(() => parseConfig('{"householdFunds":30000}'), /needs household/);
    assert.throws(() => parseConfig('{"household":"generated","householdFunds":-5}'), /integer > 0/);
    const co = await dry({ seed: 1, censusOnly: true }, content, FIXTURE, { quiet: true });
    assert.ok(co.lines.some(l => l.kind === 'census') && !co.lines.some(l => !l.kind) && co.calls.at(-1)!.cmd === 'save_game', 'census only: scratch copy, census, no stories');
    // family: a parent role picks from the child role's household only (docs/171 §12)
    const famT: Template = { id: 'fam-homework', category: 'normal', actors: [{ role: 'kid', age: ['Child', 'Teen'] }, { role: 'parent', age: ['YoungAdult', 'Adult', 'Elder'], family: 'kid' }], steps: [{ cmd: 'interact', actor: 'parent', target: '$actor.kid', label: 'Chat', waitS: 5 }] };
    const fam = await dry({ seed: 2, stories: 6 }, { ...content, templates: [famT] }, FIXTURE, { quiet: true });
    const hh = new Map<string, string>(fromAgent(FIXTURE).sims.map((s: any) => [s.name, s.household]));
    const famSteps = fam.lines.filter(l => !l.kind);
    assert.ok(famSteps.length && famSteps.every(l => hh.get(l.actor) === hh.get(l.target)), `parent from the child's household: ${famSteps.map(l => `${l.actor}/${l.target}`).join(' ')}`);
    assert.ok(checkTemplates([{ ...famT, actors: [{ role: 'parent', family: 'kid' }, { role: 'kid' }], death: 'nobody' }]).filter(e => /family must name|death must name/.test(e)).length === 2);
    // household not 'save' and creation fails: the run rejects (profile-world FAIL, exit 1), no story step runs on the save's household
    const hhCalls: string[] = [], pre = join(process.env.TEMP ?? process.env.TMPDIR ?? '.', `story-hh-check-${process.pid}`);
    const hhRun = loadStory('{"household":"full-house"}', {}).run({ ag: async (cmd: string) => { hhCalls.push(cmd); return cmd === 'make_household' ? { ok: false, error: 'funds: 26000 simoleons buy no vacant house with the essentials for 8 Sims' } : { ok: true, result: {} }; }, sleep: async () => {}, halted: () => false, pre });
    await assert.rejects(hhRun, /^Error: household creation failed: full-house: funds: 26000/);
    assert.deepEqual(hhCalls, ['make_household'], 'stopped at the failed creation');
    assert.match(readFileSync(`${pre}-story.jsonl`, 'utf8'), /"kind":"household","t":\d+,"ok":false[\s\S]*household creation failed/);
    rmSync(`${pre}-story.jsonl`, { force: true });
    console.log(`story-fuzzer selftest: PASS (${steps.length} steps in ${runs.size} stories, ${kinds('prepare').length} prepares; repo content: ${repo.content.templates.length} templates)`);
  }
}
