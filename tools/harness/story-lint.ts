// Believability lint for the story fuzzer content (docs/171 §11): rejects templates and personas a player would never see.
//   node tools/harness/story-lint.ts [--content DIR] [--seeds 5]   lint + dry-run concurrency check of the content; exit 1 on findings
//   node tools/harness/story-lint.ts --self-check                  fixture asserts (each rule fires), then the repo content
// Static rules (lintContent; story-fuzzer.ts loadContent runs them, so --check, --dry-run and profile-world --story refuse):
//   age     an action the role's ages cannot do (toddler cooking, child pregnancy, elder age-up without death, teen weddings)
//   pair    romance or fights across the Teen / adult line, pregnancy and Try for Baby without one Female and one Male role
//   occult  an occult (role or make_occult) without its EP need, or at an age that occult cannot have
//   prepare prepare: true on a kind the engine cannot make; an object kind world_census never counts (the need is never met, or
//           a prepare buys another one every run); a non-optional step target with no need (the story fails instead of skipping)
//   standin every action step optional: a run whose actions all fail still counts as that story (a silent stand-in)
//   persona ages, occults and bias keys that match no template the persona's Sims can lead
// Run rule (lintRun, CLI and self-check): one Sim in two story runs at once, from dry runs on the fake game.
// Game rules below are TS3 play knowledge [community-known, not read from the DLLs]; object kinds are the census's [verified:
// StoryHandlers.cs KindTypes, type names from the script DLL metadata].
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { fakeGame, loadContent, runStories, STORIES_DIR, type Content, type Role, type Template, type TStep } from './story-fuzzer.ts';
import { FIXTURE, fromAgent, KIND_NAMES, MOON, norm, normalize } from './story-census.ts';
import { lintDays, withDays } from './story-day.ts';
import { LABELS as REALISM, modeViolations, stepLabel, worst, type Label } from './realism.ts';

const ALL = ['Baby', 'Toddler', 'Child', 'Teen', 'YoungAdult', 'Adult', 'Elder'];
const C_UP = ALL.slice(2), T_UP = ALL.slice(3), YA_UP = ALL.slice(4), BREED = ['YoungAdult', 'Adult'];
const CATS = ['normal', 'lifestage', 'chaos', 'occult', 'world'];
// occult -> EP sets that add it (any one) and the ages it can have
export const OCCULT: Record<string, { ep: string[]; ages: string[] }> = {
  Vampire: { ep: ['LateNight', 'Supernatural'], ages: T_UP }, Werewolf: { ep: ['Supernatural'], ages: T_UP },
  Fairy: { ep: ['Supernatural'], ages: C_UP }, Witch: { ep: ['Supernatural'], ages: C_UP }, Zombie: { ep: ['Supernatural'], ages: T_UP },
  Genie: { ep: ['Showtime'], ages: BREED }, Mummy: { ep: ['WorldAdventures'], ages: ['Adult', 'Elder'] },
  Mermaid: { ep: ['IslandParadise'], ages: T_UP }, PlantSim: { ep: ['UniversityLife'], ages: C_UP },
  ImaginaryFriend: { ep: ['Generations'], ages: C_UP }, Robot: { ep: ['IntoTheFuture'], ages: BREED }, Ghost: { ep: [], ages: C_UP },
};
// interact label prefix -> ages that can do it; pair = the target role must be on the same side of the Teen / adult line;
// target = ages the target role must have; occult = the actor must be one; sexes = needs one Female and one Male role
type Rule = { re: RegExp; ages: string[]; why: string; pair?: boolean; target?: string[]; occult?: string; sexes?: boolean };
const LABELS: Rule[] = [
  { re: /^try for baby/i, ages: YA_UP, why: 'trying for a baby is YA+', pair: true, sexes: true },
  { re: /^(woohoo|propose|get married|throw wedding)/i, ages: YA_UP, why: 'WooHoo, engagement and weddings are YA+', pair: true },
  { re: /^(flirt|kiss|ask on date)/i, ages: T_UP, why: 'romance starts at Teen', pair: true },
  { re: /^fight/i, ages: T_UP, why: 'fights start at Teen', pair: true },
  { re: /^(have|group meal|bake|cook|prepare)/i, ages: T_UP, why: 'cooking is Teen+' },
  { re: /^quick meal/i, ages: C_UP, why: 'toddlers and babies are fed' },
  { re: /^(adopt|pay bills|throw a party)/i, ages: YA_UP, why: 'a household-head action (YA+)' },
  { re: /^(repair|work out|scuba)/i, ages: T_UP, why: 'Teen+ only' },
  { re: /^blow out candles/i, ages: C_UP, why: 'a toddler is helped to blow out candles' },
  { re: /^(take driving lessons|prom)/i, ages: ['Teen'], why: 'a teen-only event' },
  { re: /^go to school/i, ages: ['Child', 'Teen'], why: 'school is Child/Teen' },
  { re: /^go to work/i, ages: T_UP, why: 'jobs start at Teen' },
  { re: /^(teach to|potty train)/i, ages: T_UP, why: 'toddler skills are taught by Teen+', target: ['Toddler'] },
  { re: /^help with homework/i, ages: T_UP, why: 'Teen+ helps with homework', target: ['Child', 'Teen'] },
  { re: /^(drink|turn)\b/i, ages: T_UP, why: 'a vampire action', occult: 'Vampire' },
];
const ROMANTIC = /partner|fianc|spouse|married|romantic/i, ENGAGED = /partner|fianc|spouse|married/i;
// steps that only set the scene: a story needs one non-optional step outside these (rule standin)
const SETUP = new Set(['set_speed', 'auto_dialog', 'pending_dialogs', 'household_funds', 'cancel_interactions', 'modify_relationship', 'autonomy', 'career', 'restore_outfit']);
// agent commands that refuse outside live mode (ts3agent RequireLive, docs/171 §14)
export const LIVE_ONLY = new Set(['make_sim', 'make_occult', 'pregnancy_advance', 'start_fire', 'buy_object', 'sell_object', 'move_object', 'place_object', 'replace_object', 'set_transform', 'place_objects', 'remove_objects', 'spawn_sims', 'kill_sim', 'age_up', 'zombies', 'own_vehicle', 'place_sim']);
const PREPARABLE = new Set(['object', 'age', 'occult', 'pet', 'season', 'moon']);
const KNOWN_OBJECTS = new Set(KIND_NAMES.map(norm));
const band = (a: string) => a === 'Teen' ? 'teen' : YA_UP.includes(a) ? 'adult' : 'young';
const list = (v: unknown) => v === undefined || v === null ? [] : [v].flat().map(String);
const isAnyV = (v: unknown) => list(v).some(x => x === '*' || norm(x) === 'any');

export function lintTemplate(t: Template): string[] {
  const f: string[] = [], at = (m: string) => f.push(`${t.id}: ${m}`);
  const roles = new Map<string, Role>(t.actors.map(a => [a.role, a]));
  const ages = (r?: string) => roles.get(r ?? '')?.age ?? ALL;
  const roleOf = (v: unknown) => typeof v === 'string' ? /^\$actor\.([A-Za-z0-9_-]+)$/.exec(v)?.[1] : undefined;
  const eps = new Set(t.needs?.filter(n => n.kind === 'ep').flatMap(n => list(n.value)).map(norm));
  const bad = (r: string | undefined, ok: string[]) => r ? ages(r).filter(a => !ok.includes(a)) : [];
  const ageRule = (r: string | undefined, ok: string[], what: string) => { const x = bad(r, ok); if (x.length) at(`${what}: role ${r} may be ${x.join('/')}`); };
  // docs/171 §14: Sims and objects change only in live mode; pets have no Baby/Toddler/Teen/YoungAdult (MakePet does not check)
  let mode = '';
  for (const s of t.steps) {
    if (s.cmd === 'buy_open' || s.cmd === 'cas_open' || s.cmd === 'build_mode' && s.args?.on !== false) mode = s.cmd;
    else if (s.cmd === 'buy_close' || s.cmd === 'cas_close' || s.cmd === 'build_mode') mode = '';
    else if (mode && LIVE_ONLY.has(s.cmd) && !(mode === 'buy_open' && /^(buy|sell)_object$/.test(s.cmd))) at(`${s.cmd} after ${mode}: Sims and objects change only in live mode (close it first)`);
    // docs/236 #3: the agent answers an unasked dialog with a safe button; a step that opened one must say accept true/false
    if (s.cmd === 'auto_dialog' && typeof s.args?.accept !== 'boolean') at('auto_dialog: set args.accept (true when this story opened the dialog, false to take the safe button)');
    if (s.cmd === 'make_sim' && s.args?.species && s.args.species !== 'Human' && !['Child', 'Adult', 'Elder'].includes(s.args.age ?? 'Adult')) at(`make_sim: a ${s.args.species} cannot be ${s.args.age} (pets: Child, Adult, Elder)`);
  }
  const pair = (a: string | undefined, b: string | undefined, what: string) => {
    if (!a || !b || a === b) return;
    const bs = new Set([...ages(a), ...ages(b)].map(band));
    if (bs.size > 1) at(`${what}: roles ${a} and ${b} span ${[...bs].join(' + ')} ages (teen with teen, adult with adult)`);
  };
  const sexes = (a: string | undefined, b: string | undefined, what: string) => {
    const g = [roles.get(a ?? '')?.gender, roles.get(b ?? '')?.gender];
    if (!a || !b || !g.includes('Female') || !g.includes('Male')) at(`${what}: roles ${a ?? '?'} and ${b ?? '?'} need gender Female and Male`);
  };
  const occultRule = (type: string, r: string | undefined, where: string) => {
    const o = OCCULT[type];
    if (!o) return at(`${where}: unknown occult ${type}`);
    if (o.ep.length && !o.ep.some(e => eps.has(norm(e)))) at(`${where}: ${type} needs an ep need ${o.ep.join(' or ')}`);
    ageRule(r, o.ages, `${where} ${type}`);
  };

  for (const a of t.actors) for (const o of list(a.occult)) occultRule(o, a.role, `role ${a.role}`);
  // prepare paths
  for (const n of t.needs ?? []) {
    if (n.prepare && !PREPARABLE.has(n.kind)) at(`need ${n.kind}: prepare is not possible for this kind (only ${[...PREPARABLE].join(', ')})`);
    if (n.kind === 'object' && !isAnyV(n.value)) for (const v of list(n.value)) if (!KNOWN_OBJECTS.has(norm(v))) at(`need object ${v}: world_census never counts it (${n.prepare ? 'every run buys another' : 'never met'})`);
    if (n.kind === 'moon') for (const v of list(n.value)) if (!(norm(v) in MOON) && !/^[0-7]$/.test(v)) at(`need moon ${v}: test_env sets only full, firstquarter or 0-7`);
    if (n.kind === 'occult') for (const v of list(n.value)) occultRule(v, undefined, `need occult`);
    if (n.kind === 'age') for (const v of list(n.value)) if (!ALL.includes(v)) at(`need age ${v}: not an age`);
  }
  const covered = (k: string, v: unknown) => (t.needs ?? []).some(n => (n.kind === k || (k === 'skillclass' && n.kind === 'rabbithole')) && (isAnyV(n.value) || isAnyV(v) || list(n.value).map(norm).some(x => list(v).map(norm).includes(x))));
  let core = false;
  for (const [i, s] of t.steps.entries()) {
    const where = `step ${i} ${s.cmd}${s.label ? ` "${s.label}"` : ''}`, actor = s.actor ?? roleOf(s.args?.sim);
    const tgt = typeof s.target === 'string' ? roleOf(s.target) : roleOf(s.args?.other ?? s.args?.partner);
    if (!s.optional && !SETUP.has(s.cmd)) core = true;
    if (s.target && typeof s.target === 'object') {
      const { find, value } = s.target;
      if (find === 'object' && !isAnyV(value)) for (const v of list(value)) if (!KNOWN_OBJECTS.has(norm(v))) at(`${where}: object ${v} is not a census kind (list_objects name search may pick another object)`);
      // 'any object' always exists; anything narrower needs a need to be skipped or prepared
      if (!s.optional && !(find === 'object' && isAnyV(value)) && ['object', 'venue', 'rabbithole', 'shop', 'pet', 'skillclass'].includes(find) && !covered(find, value)) at(`${where}: target ${find} ${list(value).join('|')} has no need (the story fails instead of skipping or preparing)`);
    }
    if (s.label) for (const r of LABELS) if (r.re.test(s.label)) {
      ageRule(actor, r.ages, `${where}: ${r.why}`);
      if (r.target && tgt) ageRule(tgt, r.target, `${where}: target`);
      if (r.pair) pair(actor, tgt, where);
      if (r.sexes) sexes(actor, tgt, where);
      if (r.occult && !list(roles.get(actor ?? '')?.occult).includes(r.occult)) at(`${where}: actor ${actor} is not a ${r.occult} role`);
    }
    if (s.cmd === 'pregnancy_advance') {
      const mom = roleOf(s.args?.sim), dad = roleOf(s.args?.partner) ?? t.steps.slice(0, i).map(p => p.cmd === 'pregnancy_advance' ? roleOf(p.args?.partner) : undefined).find(Boolean);
      ageRule(mom, BREED, `${where}: pregnancy is YA/Adult`);
      if (!dad) at(`${where}: no partner role (this step or an earlier pregnancy_advance)`);
      else { ageRule(dad, YA_UP, `${where}: the father is YA+`); sexes(mom, dad, where); }
    }
    if (s.cmd === 'age_up') ageRule(roleOf(s.args?.sim), s.args?.confirm_elder_death ? ['Elder'] : ALL.filter(a => a !== 'Elder'), `${where}: ${s.args?.confirm_elder_death ? 'only an Elder dies of age' : 'an Elder does not age up'}`);
    if (s.cmd === 'make_occult') occultRule(String(s.args?.type), roleOf(s.args?.sim), where);
    if (s.cmd === 'modify_relationship' && ROMANTIC.test(String(s.args?.state ?? ''))) {
      const a = roleOf(s.args?.sim), b = roleOf(s.args?.other);
      pair(a, b, where);
      if (ENGAGED.test(String(s.args.state))) { ageRule(a, YA_UP, `${where}: ${s.args.state} is YA+`); ageRule(b, YA_UP, `${where}: ${s.args.state} is YA+`); }
    }
    if (s.cmd === 'career' && s.args?.action === 'join') ageRule(actor, T_UP, `${where}: jobs start at Teen`);
  }
  if (!core) at('every action step is optional or scene setup: a run whose actions all fail still counts as this story (silent stand-in)');
  f.push(...lintRealism(t).map(m => `${t.id}: ${m}`));
  // death (docs/171 §12): a step that kills must name its victim in `death`, else the day planner's guard fights the story
  for (const [i, s] of t.steps.entries()) {
    const victim = s.cmd === 'kill_sim' || (s.cmd === 'age_up' && s.args?.confirm_elder_death) ? roleOf(s.args?.sim) ?? s.actor : undefined;
    if ((s.cmd === 'kill_sim' || (s.cmd === 'age_up' && s.args?.confirm_elder_death)) && t.death !== victim) at(`step ${i} ${s.cmd}: kills ${victim ?? 'a Sim'} but the template's death is ${t.death ?? 'unset'} (set "death": "${victim ?? '<role>'}")`);
  }
  // family: a parent role next to a child role comes from the child's household
  const young = t.actors.filter(a => a.age?.length && a.age.every(x => ['Baby', 'Toddler', 'Child', 'Teen'].includes(x)));
  for (const a of t.actors) if (/^parent/i.test(a.role) && young.length && !a.family) at(`role ${a.role}: a parent of ${young.map(y => y.role).join('/')} needs "family": "${young[0].role}" (else any adult in town)`);
  return f;
}

// gameplay realism (docs/179 §3): the template's `realism` label (natural | cheat | stress) is declared and not lower than its
// steps' labels from docs/data/gameplay-preconditions.csv; steps run in a game mode their command allows; player-only rules
// (a player directs only the active household, a cake ages a Sim once, University and vacations are YA+) [docs/179 sources]
const CAS_OCCULT = new Set(['Vampire', 'Werewolf', 'Witch', 'Fairy', 'Ghost', 'Genie', 'Mermaid', 'Robot']); // docs/179 R-OCC-1
const DIRECT = new Set(['interact', 'any_interaction', 'go_here', 'sim_visit_lot', 'sim_route_to', 'do_interaction']);
export function templateLabel(t: Template): { label: Label; why: string[] } {
  const roles = new Map<string, Role>(t.actors.map(a => [a.role, a]));
  const ls = t.steps.map(s => {
    if (s.cmd === 'interact' || s.cmd === 'any_interaction') return stepLabel({ cmd: 'do_interaction' });
    if (s.cmd === 'make_occult') { // a new Sim with a CAS occult is a CAS creation; anything else is an instant transform
      const r = roles.get(s.actor ?? /^\$actor\.(.+)$/.exec(String(s.args?.sim ?? ''))?.[1] ?? '');
      return r?.pick === 'new' && CAS_OCCULT.has(String(s.args?.type)) ? 'natural' : 'cheat';
    }
    return stepLabel(s);
  });
  for (const a of t.actors) if (a.pick === 'new') for (const o of list(a.occult)) if (!CAS_OCCULT.has(o)) ls.push('cheat');
  const label = worst(ls);
  return { label, why: [...new Set(t.steps.filter((_, i) => ls[i] === label && label !== 'natural').map(s => s.cmd))] };
}
export function lintRealism(t: Template): string[] {
  const f: string[] = [], roles = new Map<string, Role>(t.actors.map(a => [a.role, a]));
  const decl = (t as any).realism, { label, why } = templateLabel(t);
  if (!REALISM.includes(decl)) f.push(`realism: declare "realism": "${label}" (natural | cheat | stress)`);
  else if (REALISM.indexOf(decl) < REALISM.indexOf(label)) f.push(`realism ${decl}, but ${why.join(', ')} make it ${label}`);
  const steps = t.steps.map(s => s.cmd === 'interact' || s.cmd === 'any_interaction' ? { ...s, cmd: 'do_interaction' } : s);
  for (const m of modeViolations(steps)) f.push(`mode: ${m}`);
  const actorOf = (s: TStep) => s.actor ?? /^\$actor\.(.+)$/.exec(String(s.args?.sim ?? ''))?.[1];
  for (const [i, s] of t.steps.entries()) {
    const r = roles.get(actorOf(s) ?? '');
    if (DIRECT.has(s.cmd) && r?.pick === 'townie' && decl !== 'stress') f.push(`step ${i} ${s.cmd}: a player directs only the active household; townie role ${r.role} acting needs realism stress`);
    const tr = r ?? t.actors[0]; // travel steps without a sim send the active Sim: the lead role
    if ((s.cmd === 'travel_university' || s.cmd === 'travel_vacation') && tr) { const x = tr.age?.filter(a => !YA_UP.includes(a)) ?? []; if (x.length) f.push(`step ${i} ${s.cmd}: role ${tr.role} may be ${x.join('/')} (YA+ only)`); }
    if (s.cmd === 'age_up' && t.steps.slice(0, i).some(p => /^blow out candles/i.test(p.label ?? '') && actorOf(p) === actorOf(s))) f.push(`step ${i} age_up after Blow Out Candles on the same Sim: ages twice when the cake works`);
  }
  return f;
}

export function lintContent(c: Content): string[] {
  const f = c.templates.flatMap(lintTemplate);
  const leads = (ages: string[]) => c.templates.filter(t => !t.actors.length || !t.actors[0].age || t.actors[0].age.some(a => ages.includes(a)));
  const biasKeys = (who: string, b: { categoryBias?: Record<string, number>; templateBias?: Record<string, number> }, ages: string[]) => {
    for (const k of Object.keys(b.categoryBias ?? {})) if (!CATS.includes(k)) f.push(`${who}: categoryBias ${k} is not a category`);
    for (const k of Object.keys(b.templateBias ?? {})) {
      if (!c.templates.some(t => t.id.startsWith(k))) f.push(`${who}: templateBias ${k} matches no template`);
      else if (!leads(ages).some(t => t.id.startsWith(k))) f.push(`${who}: templateBias ${k}: no ${ages.join('/')} Sim can lead those templates`);
    }
  };
  for (const p of c.personas) {
    const h: any = p.household ?? {}, ages: string[] = h.ages ?? ALL;
    for (const a of ages) if (!ALL.includes(a)) f.push(`persona ${p.id}: age ${a} is not an age`);
    for (const o of list(h.occult)) if (!OCCULT[o]) f.push(`persona ${p.id}: occult ${o} is not an occult`);
    biasKeys(`persona ${p.id}`, p, ages);
  }
  for (const [w, b] of Object.entries(c.worlds)) biasKeys(`world ${w}`, b, ALL);
  return f;
}

// one Sim in two story runs at once: step lines (actor, and a target that is a Sim) grouped per run, run = first step start ..
// last step end; shortcut: the wait after a run's last step is not logged, so this finds overlaps of the logged calls only
export function lintRun(lines: any[], simNames: Set<string>): string[] {
  const runs = new Map<string, { a: number; b: number; sims: Set<string> }>();
  for (const l of lines) {
    if (l.kind || typeof l.story !== 'string') continue;
    const k = `${l.story}#${l.run}`, r = runs.get(k) ?? runs.set(k, { a: l.t, b: l.t, sims: new Set() }).get(k)!;
    r.a = Math.min(r.a, l.t); r.b = Math.max(r.b, l.t + (l.ms ?? 0));
    for (const s of [l.actor, l.target]) if (typeof s === 'string' && simNames.has(s)) r.sims.add(s);
  }
  const f: string[] = [], rs = [...runs];
  for (let i = 0; i < rs.length; i++) for (let j = i + 1; j < rs.length; j++) {
    const [ka, a] = rs[i], [kb, b] = rs[j];
    if (a.a < b.b && b.a < a.b) for (const s of a.sims) if (b.sims.has(s)) f.push(`${s} in ${ka} and ${kb} at once`);
  }
  return f;
}

// engine side of docs/179 R-MODE-2: no story step or prepare goes out while a between-story build mode is open (the
// 0x575651 crash was a make_sim prepare in build mode); shortcut: logged start times only, so a call sent just before the
// switch and still running in it is not seen
export function lintModes(lines: any[]): string[] {
  const wins = lines.filter(l => l.kind === 'between' && l.cmd === 'build_mode').map(l => [l.t, l.t + (l.ms ?? 0)]);
  return lines.filter(l => (!l.kind || l.kind === 'prepare') && wins.some(([a, b]) => l.t > a && l.t < b))
    .map(l => `${l.kind ?? 'step'} ${l.story ?? ''} ${l.cmd ?? l.need ?? ''} sent while build mode was open`);
}

async function dryLines(content: Content, seed: number, minutes = 30) {
  const g = fakeGame(FIXTURE), lines: any[] = [];
  await g.drive(runStories({ ag: g.ag, sleep: g.sleep, now: g.now, halted: () => false, census: normalize(FIXTURE, 'fixture'), log: l => lines.push(l) }, { seed, minutes }, content));
  const names = new Set<string>([...FIXTURE.sims.map(s => s.name), ...lines.flatMap(l => l.calls ?? []).flatMap((c: any) => c.sim ? [c.sim] : [])]);
  return { lines, names };
}
export async function lintDry(content: Content, seeds: number[]) {
  const f: string[] = [];
  for (const s of seeds) { const { lines, names } = await dryLines(content, s); f.push(...[...lintRun(lines, names), ...lintModes(lines)].map(x => `seed ${s}: ${x}`)); }
  // one day-planner run (docs/171 §12): rescues per Sim-day and stories started on Sims at work, school or asleep
  const g = withDays(fakeGame(FIXTURE), fromAgent(FIXTURE).sims) as ReturnType<typeof fakeGame>, lines: any[] = [];
  await g.drive(runStories({ ag: g.ag, sleep: g.sleep, now: g.now, halted: () => false, census: normalize(FIXTURE, 'fixture'), log: l => lines.push(l) }, { seed: seeds[0] ?? 1, minutes: 25, days: true }, content));
  f.push(...lintDays(lines).map(x => `days seed ${seeds[0] ?? 1}: ${x}`));
  return f;
}

async function selfCheck() {
  const T = (id: string, actors: Role[], steps: TStep[], needs: any[] = []): Template => ({ id, category: 'normal', realism: 'stress', actors, needs, steps } as Template);
  const has = (t: Template, m: RegExp) => assert.ok(lintTemplate(t).some(x => m.test(x)), `${t.id}: expected ${m} in ${lintTemplate(t).join(' / ')}`);
  // live mode only (docs/171 §14), except buying and selling inside Buy mode (docs/171 §16); no make_sim in Buy mode, no toddler dog
  assert.deepEqual(lintTemplate(T('buy-in-buy', [], [{ cmd: 'buy_open' }, { cmd: 'buy_object', args: { product: 'random' } }, { cmd: 'sell_object' }, { cmd: 'buy_close' }])), []);
  has(T('sim-in-buy', [], [{ cmd: 'buy_open' }, { cmd: 'make_sim' }, { cmd: 'buy_close' }]), /make_sim after buy_open/);
  has(T('buy-in-build', [], [{ cmd: 'build_mode', args: { on: true } }, { cmd: 'buy_object', args: { product: 'random' } }, { cmd: 'build_mode', args: { on: false } }]), /buy_object after build_mode/);
  has(T('pup', [], [{ cmd: 'make_sim', args: { species: 'Dog', age: 'Toddler' } }]), /a Dog cannot be Toddler/);
  assert.deepEqual(lintTemplate(T('buy-ok', [], [{ cmd: 'buy_open' }, { cmd: 'buy_close' }, { cmd: 'buy_object', args: { product: 'random' } }, { cmd: 'make_sim', args: { species: 'Cat' } }])), []);
  // docs/236 #3: auto_dialog must say whether the story expects accept
  has(T('dlg-default', [], [{ cmd: 'auto_dialog', args: {} }]), /auto_dialog: set args\.accept/);
  assert.ok(!lintTemplate(T('dlg-accept', [], [{ cmd: 'auto_dialog', args: { accept: true } }])).some(x => /auto_dialog/.test(x)));
  // age: toddler cooking, child pregnancy, elder age-up, teen wedding
  has(T('tod-cook', [{ role: 'c', age: ['Toddler', 'Adult'] }], [{ cmd: 'interact', actor: 'c', label: 'Have Dinner', target: { find: 'object', value: 'fridge' } }], [{ kind: 'object', value: 'fridge' }]), /cooking is Teen\+: role c may be Toddler/);
  has(T('kid-preg', [{ role: 'm', age: ['Child'], gender: 'Female' }, { role: 'd', age: ['Adult'], gender: 'Male' }], [{ cmd: 'pregnancy_advance', args: { sim: '$actor.m', partner: '$actor.d', to: 'birth' } }]), /pregnancy is YA\/Adult: role m may be Child/);
  has(T('no-dad', [{ role: 'm', age: ['Adult'], gender: 'Female' }], [{ cmd: 'pregnancy_advance', args: { sim: '$actor.m', to: 'labor' } }]), /no partner role/);
  has(T('same-sex', [{ role: 'm', age: ['Adult'] }, { role: 'd', age: ['Adult'] }], [{ cmd: 'pregnancy_advance', args: { sim: '$actor.m', partner: '$actor.d', to: 'birth' } }]), /need gender Female and Male/);
  has(T('old-cake', [{ role: 's', age: ['Adult', 'Elder'] }], [{ cmd: 'age_up', args: { sim: '$actor.s' } }]), /Elder does not age up/);
  has(T('teen-flirt', [{ role: 'a', age: ['Teen'] }, { role: 'b', age: ['Adult'] }], [{ cmd: 'interact', actor: 'a', target: '$actor.b', label: 'Flirt' }]), /span teen \+ adult/);
  // occult without the EP, or at a wrong age
  has(T('vamp-noep', [{ role: 'v', age: ['Adult'], occult: 'Vampire' }], [{ cmd: 'any_interaction', actor: 'v', target: { find: 'object', value: 'any' } }]), /Vampire needs an ep need LateNight or Supernatural/);
  has(T('genie-kid', [{ role: 'g', age: ['Child'] }], [{ cmd: 'make_occult', args: { sim: '$actor.g', type: 'Genie' } }], [{ kind: 'ep', value: 'Showtime' }]), /Genie: role g may be Child/);
  // prepare paths and stand-ins
  has(T('prep-venue', [], [{ cmd: 'zombies' }], [{ kind: 'venue', value: 'Beach', prepare: true }]), /prepare is not possible/);
  has(T('blind-obj', [{ role: 'a', age: ['Adult'] }], [{ cmd: 'any_interaction', actor: 'a', target: { find: 'object', value: 'stove' } }]), /has no need/);
  has(T('no-census', [{ role: 'a', age: ['Adult'] }], [{ cmd: 'zombies' }], [{ kind: 'object', value: 'jukebox', prepare: true }]), /never counts it \(every run buys another\)/);
  has(T('all-opt', [{ role: 'a', age: ['Adult'] }], [{ cmd: 'household_funds', args: { add: 1 } }, { cmd: 'cheat', args: { command: 'x' }, optional: true }]), /silent stand-in/);
  assert.deepEqual(lintTemplate(T('fine', [{ role: 'm', age: BREED, gender: 'Female' }, { role: 'd', age: YA_UP, gender: 'Male' }], [{ cmd: 'pregnancy_advance', args: { sim: '$actor.m', partner: '$actor.d', to: 'pregnant' } }, { cmd: 'pregnancy_advance', args: { sim: '$actor.m', to: 'birth' } }])), []);
  // death and family (docs/171 §12)
  has(T('kill-noowner', [{ role: 'v', age: ['Adult'] }], [{ cmd: 'kill_sim', args: { sim: '$actor.v', death: 'Burn' } }]), /kills v but the template's death is unset/);
  has(T('old-noowner', [{ role: 'e', age: ['Elder'] }], [{ cmd: 'age_up', args: { sim: '$actor.e', confirm_elder_death: true } }]), /set "death": "e"/);
  assert.deepEqual(lintTemplate({ ...T('kill-ok', [{ role: 'v', age: ['Adult'] }], [{ cmd: 'kill_sim', args: { sim: '$actor.v', death: 'Burn' } }]), death: 'v' }), []);
  has(T('stranger-parent', [{ role: 'kid', age: ['Child'] }, { role: 'parent', age: ['Adult'] }], [{ cmd: 'interact', actor: 'parent', target: '$actor.kid', label: 'Help with Homework' }]), /needs "family": "kid"/);
  // gameplay realism (docs/179): label declared and high enough, game modes, player-only rules
  const R = (id: string, realism: string | undefined, actors: Role[], steps: TStep[]) => ({ ...T(id, actors, steps), realism }) as Template;
  has(R('no-label', undefined, [{ role: 'a', age: ['Adult'] }], [{ cmd: 'go_here', actor: 'a' }]), /declare "realism": "natural"/);
  has(R('low-label', 'natural', [{ role: 'a', age: ['Adult'] }], [{ cmd: 'set_motive', args: { sim: '$actor.a', motive: 'Hunger', value: -80 } }, { cmd: 'go_here', actor: 'a' }]), /realism natural, but set_motive make it cheat/);
  has(R('build-sim', 'natural', [], [{ cmd: 'build_mode', args: { on: true } }, { cmd: 'make_sim', args: { age: 'Adult' } }, { cmd: 'build_mode', args: { on: false } }]), /mode: step 1 make_sim in Build/);
  has(R('townie-acts', 'natural', [{ role: 'a', age: ['Adult'] }, { role: 'b', age: ['Adult'], pick: 'townie' }], [{ cmd: 'interact', actor: 'b', target: '$actor.a', label: 'Chat' }]), /directs only the active household/);
  has(R('teen-uni', 'natural', [{ role: 's', age: ['Teen', 'YoungAdult'] }], [{ cmd: 'travel_university', actor: 's' }]), /role s may be Teen \(YA\+ only\)/);
  has(R('cake-twice', 'cheat', [{ role: 's', age: ['Child'] }], [{ cmd: 'interact', actor: 's', label: 'Blow Out Candles', optional: true }, { cmd: 'age_up', args: { sim: '$actor.s' } }]), /ages twice/);
  assert.equal(templateLabel(R('cas-vamp', 'natural', [{ role: 'v', age: ['Adult'], pick: 'new', occult: 'Vampire' }], [{ cmd: 'make_occult', args: { sim: '$actor.v', type: 'Vampire' } }])).label, 'natural', 'a new CAS vampire is a CAS creation');
  assert.equal(templateLabel(R('bite-cheat', 'cheat', [{ role: 'v', age: ['Adult'], pick: 'driven' }], [{ cmd: 'make_occult', args: { sim: '$actor.v', type: 'Vampire' } }])).label, 'cheat', 'an existing Sim turned instantly');
  assert.deepEqual(lintModes([{ kind: 'between', cmd: 'build_mode', t: 1000, ms: 9000 }, { kind: 'prepare', t: 2000, need: 'age' }, { story: 's', cmd: 'go_here', t: 12000 }]), ['prepare  age sent while build mode was open']);
  // personas and world biases
  const pc = lintContent({ templates: [T('skill-chess', [{ role: 'a', age: ['Adult'] }], [{ cmd: 'zombies' }])], categories: {}, worlds: { x: { templateBias: { 'social-party': 2 } } },
    personas: [{ id: 'kid', household: { ages: ['Child', 'Kid'] } as any, categoryBias: { party: 2 }, templateBias: { 'skill-chess': 2 } }] });
  for (const m of [/age Kid is not an age/, /categoryBias party/, /skill-chess: no Child\/Kid Sim can lead/, /world x: templateBias social-party matches no template/]) assert.ok(pc.some(x => m.test(x)), `${m} in ${pc.join(' / ')}`);
  // run rule: a Sim in two overlapping runs
  const L = (story: string, run: number, t: number, actor: string, target?: string) => ({ story, run, t, ms: 100, actor, target });
  assert.deepEqual(lintRun([L('a', 1, 0, 'Ann'), L('a', 1, 5000, 'Ann'), L('b', 2, 2000, 'Bob', 'Ann'), L('c', 3, 9000, 'Ann')], new Set(['Ann', 'Bob'])), ['Ann in a#1 and b#2 at once']);
  // the census kinds the lint trusts are the ones the agent counts
  const cs = readFileSync(join(import.meta.dirname, '..', '..', 'mods', 'ts3agent', 'src', 'plugin', 'Handlers', 'StoryHandlers.cs'), 'utf8');
  const kt = /KindTypes = \{([\s\S]*?)\n\s*\};/.exec(cs)![1];
  assert.deepEqual([...kt.matchAll(/new string\[\] \{ "(\w+)"/g)].map(m => m[1]), KIND_NAMES, 'story-census KIND_NAMES = StoryHandlers.cs KindTypes');
  // the repo content is clean, statically and in dry runs
  const repo = loadContent(STORIES_DIR);
  assert.deepEqual(repo.errors, [], 'repo content');
  assert.deepEqual(await lintDry(repo.content, [1, 2, 3]), [], 'dry runs: one Sim per story at a time');
  console.log(`story-lint self-check ok (${repo.content.templates.length} templates, ${repo.content.personas.length} personas clean; 3 dry-run seeds)`);
}

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2), opt = (k: string, d: string) => { const i = args.indexOf(k); return i < 0 ? d : args[i + 1]; };
  if (args.includes('--self-check')) await selfCheck();
  else {
    const dir = resolve(opt('--content', STORIES_DIR)), { content, errors } = loadContent(dir);
    const seeds = Array.from({ length: Number(opt('--seeds', '5')) }, (_, i) => i + 1);
    const f = [...errors, ...(errors.length ? [] : await lintDry(content, seeds))];
    console.log(`${content.templates.length} templates, ${content.personas.length} personas in ${dir}: ${f.length ? `${f.length} findings` : `clean (static + ${seeds.length} dry-run seeds)`}`);
    for (const x of f) console.log(`  ${x}`);
    process.exit(f.length ? 1 : 0);
  }
}
