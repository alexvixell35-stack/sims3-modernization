"""Leak stress for soak runs (issue #40; user 11-08: "make every sim a celebrity and make them talk to each other ... get to
know everyone and lose"): beside a soak, townies (never the active household: pick_sims townies:true, make_celebrity skips
it) go through the life events whose listeners and tables leaked in the scans, at fixed rates from a seeded RNG so both
arms of an A/B get the same schedule (the game's own outcomes still differ). From --start minutes after <start file>:
  - celebrity:  every 20 min every townie that can be one gets celebrity level 1..5 (cycling), every 3rd round level 0
                (status removed) first: EA's forceSetLevel cheat loop (make_celebrity all:true).
  - meet/lose:  every 10 min 16 instantiated townies; every pair gets a relationship (Relationship.Get + ForceChangeState),
                alternating rounds: meet (Acquaintance / Friend / GoodFriend / RomanticInterest for adult pairs) and lose
                (Disliked / Enemy / Ex for adult pairs / Stranger, liking -100).
  - socials:    every 1 min 8 instantiated townies in 4 pairs; each actor queues an EA social on its partner (agent
                social: SocialInteractionA by name, SOCIALS; romantic ones for adult pairs only). The pie menu
                (list_interactions) offers townie pairs no socials at all (al337-al339: 0 enabled).
  - deaths:     every 4 min one townie YA/adult/elder dies (kill_sim, death types rotate), max --max.
  - occults:    every 12 min one townie becomes a Werewolf / Fairy / Vampire / Witch (rotating).
  - aging:      every 9 min one townie child/teen/YA/adult ages up (never an elder: that is a death).
  - jobs:       every 15 min one townie YA/adult quits its job (career quit).
  - movers:     every 20 min spawn_sims 6 (new townies in non-active households), 15 min later remove_sims (disposed).
  - outfits:    every 1 min 10 townies (from a random start in the world list) switch outfit category (outfit_change
                townies:true; athletic / formal / sleepwear / swimwear / everyday rotating).
  - promote:    every 3 min 3 townies are promoted (a townie without a job joins one first), every 4th round demoted.
  - party:      every 15 min a townie throws a house party with 12 townie guests (throw_party, 30 min ahead).
  - crowd:      every 12 min 20 townies go to the same community lot (sim_visit_lot), the lot rotating.
  - skill:      every 5 min 3 townies get a random skill set to a random level.
  - lifecycle:  every 6 min one townie ages up a stage; at elder the next age-up is its old-age death; then a new one.
  - move:       every 10 min a townie household moves to a free furnished house (move_household).
  - story:      EA story progression set to Accelerated with 60 actions per story day; calls alternate two
                story days (story_days) and a 60-action burst without EA's category cooldowns (story_burst), every 30 s;
                skipped while the last batch runs; EA's actions skip the active household.
  - season:     every 10 min the world's season changes (test_env fixed; summer / fall / spring rotating, never winter).
  - traffic:    every 5 min 10 driverless traffic cars (spawn_traffic; EA's TrafficManager cars with LiteDriver tasks).
  - fight:      every 3 min 8 townies YA/adult/elder in 4 pairs: made Enemies (liking -100), then one queues Fight! on
                the other (agent social "Force Fight!", the forced fight EA itself pushes).
  - pregnancy:  every 15 min a young adult or adult female townie gets pregnant by a male townie (pregnancy_advance
                to pregnant: Pregnancy.Start); the next round takes that mother to birth (HaveTheBaby), then a new one.
Only live mode; every 20 min the ts3fixes leak counters. Stops when <run>.out says done or the game is gone.
--busy-file <path>: for a run beside another script that drives the game in cycles and holds that file while it does
(a local leak-modes runner, al339): before each batch of due events it waits while the
file is there (at most 10 min, logged past 30 s); before every agent command it sets <path>.stress, then looks again: if
the file is back, no command goes out, the batch stops and its unfinished events stay due. --self-check: tests that with
a temp dir and a fake agent (no game), exits 0.
Usage: python -I tools/harness/leak-stress-drip.py <start file> <run .out file> [--start 10] [--max 30] [--seed 12351] [--busy-file <path>]
       python -I tools/harness/leak-stress-drip.py --self-check"""
import json
import os
import random
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request

DEATHS = ["Electrocution", "Starve", "Drown", "Burn", "OldAge"]
OCCULTS = ["Werewolf", "Fairy", "Vampire", "Witch"]
OUTFITS = ["athletic", "formal", "sleepwear", "swimwear", "everyday"]
MEET = ["Acquaintance", "Friend", "GoodFriend"]
LOSE = ["Disliked", "Enemy", "Stranger"]
SKILLS = ["Charisma", "Logic", "Cooking", "Athletic", "Painting", "Guitar", "Writing", "Gardening", "Fishing", "Handiness"]
LOTS = ["far", "far2"]
SEASONS = ["summer", "fall", "spring"]
# EA social (ActionData) names; one the game does not know is dropped at its first "no EA social named" answer
SOCIALS = ["Chat", "Tell Joke", "Compliment", "Brag", "Gossip", "Hug", "Insult", "Argue", "Slap", "Flirt", "Kiss"]
ROMANTIC = ("Flirt", "Kiss")
COUNTERS = ["errortrapsims_dropped", "celebritydeadsim_unregistered", "seasonsdeadsim_dead", "seasonsdeadsim_replaced",
            "ghostreactdeadsim_removed", "casuihandlers_closed", "casuihandlers_dups", "errortraplookup_dropped",
            "dreamnodedeadsim_removed", "overwatchdeadsim_removed", "errortrapctl_dropped", "outfitwaitdead_ended", "outfitwaitdead_saved",
            "simrefsdeadsim_talkingto", "simrefsdeadsim_pusher", "simrefsdeadsim_reactors",
            "simrefsdeadsim_occultowners", "outfitwaitsaved_destroyed"]


class Busy(Exception):
    """the other runner took the busy file during a batch"""


BUSY = None  # --busy-file
ARMED = False  # the busy file was gone when this batch started, so its return stops the batch


def ag(cmd, args=None, timeout=8000):
    if not BUSY:
        return _ag(cmd, args, timeout)
    flag = BUSY + ".stress"
    open(flag, "w").close()  # our flag first, then their file; the other runner does the reverse, so one side always sees the other
    try:
        if ARMED and os.path.exists(BUSY):
            raise Busy(cmd)
        return _ag(cmd, args, timeout)
    finally:
        try:
            os.remove(flag)
        except OSError:
            pass


def wait_free(path, out_file=None, max_s=600):
    """waits while the busy file is there, at most max_s (or until the run is done); True when it is gone"""
    t0 = time.time()
    while os.path.exists(path) and time.time() - t0 < max_s and not (out_file and done(out_file)):
        time.sleep(1)
    w = time.time() - t0
    if w > 30:
        say(f"busy file: waited {w:.0f} s{', still there: going on' if os.path.exists(path) else ''}")
    return not os.path.exists(path)


def _ag(cmd, args=None, timeout=8000):
    q = {"cmd": cmd, "timeout": str(timeout)}
    if args is not None:
        q["args"] = json.dumps(args)
    try:
        with urllib.request.urlopen("http://127.0.0.1:47891/agent?" + urllib.parse.urlencode(q), timeout=timeout / 1000 + 25) as r:
            return json.load(r)
    except Exception as e:  # game loading, agent busy
        return {"ok": False, "error": str(e)}


def say(s):
    print(time.strftime("%H:%M:%S") + " " + s, flush=True)


def err(r):
    return str(r.get("error"))[:140]


def game_up():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout
    return re.search(r"^TS3W\.exe", out, re.M | re.I) is not None


def done(out_file):  # the chain's own end line, "<run> done" / "<run> skipped" (<run> = the .out file's name)
    run = re.escape(os.path.splitext(os.path.basename(out_file))[0])
    try:
        return re.search(rf"\b{run} (done|skipped)\b", open(out_file, encoding="utf-8", errors="replace").read()) is not None
    except OSError:
        return False


def live():
    st = ag("game_state")
    return st.get("ok") and (st.get("result") or {}).get("sub_state") == "LiveModeState"


def pick(age, count):
    """the picked townies' names in world order (pick_sims stores them as picked:1..n), [] if none"""
    p = ag("pick_sims", {"age": age, "count": count, "townies": True})
    if not p.get("ok"):
        return []
    res = p.get("result") or {}
    got = res.get("picked") if isinstance(res, dict) else res
    return got if isinstance(got, list) else []


def social(actor, target, name):
    """agent social; True when queued. Drops a name the game does not know from SOCIALS."""
    r = ag("social", {"sim": actor, "target": target, "name": name}, 10000)
    if not r.get("ok") and "no EA social named" in err(r) and name in SOCIALS:
        SOCIALS.remove(name)
    return bool(r.get("ok") and (r.get("result") or {}).get("queued", True)), r


SR_LAST = [None]  # kind of the previous social_round call: its "last" report covers that call


def social_round(args, kind, n, fails):
    """agent social_round (SocialRoundHandlers.cs): nearest townie pairs queue a social from their own menu; its "last"
    says how the previous call's socials ended (started = EA's StandardEntry ran, so the social really played)"""
    r = ag("social_round", args, 20000)
    if not r.get("ok"):
        if fails["social_round"] < 6:
            fails["social_round"] += 1
            say(f"social_round {kind} failed: {err(r)}")
        return
    res = r.get("result") or {}
    last = res.get("last") or {}
    if SR_LAST[0] and isinstance(last, dict):
        for f in ("socials", "started", "cancelled", "dropped"):
            n[f"sr_{f}"] = n.get(f"sr_{f}", 0) + (last.get(f) or 0)
    q = res.get("queued") or []
    n["sr_queued"] = n.get("sr_queued", 0) + len(q)
    say(f"social_round {kind}: {len(q)} queued of {res.get('candidates')} townies, skipped {json.dumps(res.get('skipped'))[:120]};"
        f" last ({SR_LAST[0]}): {json.dumps({k: v for k, v in last.items() if k != 'ran'})[:220] if isinstance(last, dict) else last}")
    SR_LAST[0] = kind


def main():
    global BUSY, ARMED
    a = sys.argv[1:]
    if a == ["--self-check"]:
        self_check()
        return
    if len(a) < 2:
        print(__doc__)
        sys.exit(2)
    start_file, out_file = a[0], a[1]
    BUSY = a[a.index("--busy-file") + 1] if "--busy-file" in a else None
    opt = {k: float(a[a.index(k) + 1]) for k in ("--start", "--max", "--seed") if k in a}
    only = set(a[a.index("--only") + 1].split(",")) if "--only" in a else None
    start_m, max_deaths, seed = opt.get("--start", 10), int(opt.get("--max", 30)), int(opt.get("--seed", 12351))
    rng = random.Random(seed)
    while not os.path.exists(start_file):
        if done(out_file):
            return
        time.sleep(20)
    t0 = os.path.getmtime(start_file)
    say(f"stress armed: from {start_m:g} min after {start_file}, seed {seed}, max deaths {max_deaths}" + (f", busy file {BUSY}" if BUSY else ""))
    # event -> (first minute after start_m, period minutes)
    plan = {"social": (0, 1), "meet": (1, 10), "celebrity": (2, 20), "death": (5, 4), "occult": (7, 12), "age": (9, 9),
            "job": (11, 15), "spawn": (13, 20), "outfit": (0.5, 1), "pregnancy": (4, 15),
            "promote": (1.5, 3), "party": (3, 15), "crowd": (6, 12), "skill": (2.5, 5), "lifecycle": (3.5, 6), "fight": (2, 3),
            "move": (8, 10), "story": (0, 0.5), "adopt": (4.5, 8), "giveaway": (9.5, 10), "season": (6, 10), "traffic": (3, 5)}
    if only:
        plan = {k: v for k, v in plan.items() if k in only}  # story spam is in every run (user 11-09: "all agents can have this")
    else:
        # shortcut: pet commands never ran in game (pet Sim Destroy without FadeOut); default plan gets them after a focus run passes
        plan = {k: v for k, v in plan.items() if k not in ("adopt", "giveaway")}
    nxt = {k: t0 + (start_m + f) * 60 for k, (f, _) in plan.items()}
    n = {k: 0 for k in plan}
    fails = {k: 0 for k in plan}
    fails["social_round"] = 0
    remove_at = None
    mother = None
    doomed = None
    last_c = 0
    while not done(out_file):
        if BUSY:
            ARMED = wait_free(BUSY, out_file)  # still there after 10 min (a stale file): this batch does not stop for it
        snap = None  # nxt before the event in progress: a Busy puts it back, so that event stays due
        try:
            now = time.time()
            if now - last_c > 1200:
                last_c = now
                r = ag("patches")
                if r.get("ok"):
                    say("counters: " + " ".join(f"{k}={r['result'].get(k)}" for k in COUNTERS))
            due = [k for k in plan if now >= nxt[k]] + (["remove"] if remove_at and now >= remove_at else [])
            if not due:
                time.sleep(5)
                continue
            if not game_up():
                if now - t0 > 600:
                    say("game gone, stopping")
                    break
                time.sleep(10)
                continue
            if not live():
                time.sleep(15)
                continue
            for k in due:
                snap = dict(nxt)
                if k == "remove":
                    r = ag("remove_sims", {}, 20000)
                    say(f"movers removed: {json.dumps(r.get('result'))[:160] if r.get('ok') else 'failed ' + err(r)}")
                    remove_at = None
                    continue
                nxt[k] = time.time() + plan[k][1] * 60
                ok = True
                if k == "social":
                    adult_pair = n[k] % 2 == 0  # alternate: adult rounds (romance allowed) and rounds with teens
                    ps = pick("youngadult,adult,elder" if adult_pair else "teen,youngadult,adult,elder", 40)
                    idx = rng.sample(range(1, len(ps) + 1), min(16, len(ps)))
                    done_n = 0
                    for i in range(0, len(idx) - 1, 2):
                        names_ = [s for s in SOCIALS if adult_pair or s not in ROMANTIC]
                        if not names_:
                            break
                        name_ = rng.choice(names_)
                        q, r = social(f"picked:{idx[i]}", f"picked:{idx[i + 1]}", name_)
                        if q:
                            done_n += 1
                        elif fails[k] < 12:
                            fails[k] += 1
                            say(f"social {name_} failed: {err(r) if not r.get('ok') else 'queue refused it'}")
                    n[k] += 1
                    n["socials_queued"] = n.get("socials_queued", 0) + done_n
                    social_round({"pairs": 12}, "social", n, fails)  # nearest pairs, every category; reports what really ran
                elif k == "meet":
                    adult_round = (n[k] // 2) % 2 == 0  # meet/lose with adults (romance, Ex), then meet/lose with children and teens too
                    lose = n[k] % 2 == 1
                    ps = pick("youngadult,adult,elder" if adult_round else "child,teen,youngadult,adult,elder", 40)
                    idx = rng.sample(range(1, len(ps) + 1), min(16, len(ps)))
                    pairs = 0
                    for i in range(len(idx)):
                        for j in range(i + 1, len(idx)):
                            if lose:
                                st = rng.choice(LOSE + (["Ex"] if adult_round else []))
                                args = {"sim": f"picked:{idx[i]}", "other": f"picked:{idx[j]}", "state": st, "liking": -100}
                            else:
                                st = rng.choice(MEET + (["RomanticInterest"] if adult_round else []))
                                args = {"sim": f"picked:{idx[i]}", "other": f"picked:{idx[j]}", "state": st, "liking": rng.choice([30, 60, 90])}
                            r = ag("modify_relationship", args)
                            if r.get("ok"):
                                pairs += 1
                            elif fails[k] < 12:
                                fails[k] += 1
                                say(f"relationship failed: {err(r)}")
                    n[k] += 1
                    say(f"{'lose' if lose else 'meet'} round {n[k]} ({'adults' if adult_round else 'all ages'}): {pairs} pairs among {len(idx)} townies")
                elif k == "celebrity":
                    if n[k] % 3 == 2:
                        r0 = ag("make_celebrity", {"all": True, "level": 0}, 20000)
                        say(f"celebrity status removed: {json.dumps(r0.get('result')) if r0.get('ok') else 'failed ' + err(r0)}")
                    lvl = n[k] % 5 + 1
                    r = ag("make_celebrity", {"all": True, "level": lvl}, 20000)
                    say(f"celebrity round {n[k] + 1} level {lvl}: {json.dumps(r.get('result')) if r.get('ok') else 'failed ' + err(r)}")
                    n[k] += 1
                elif k == "death":
                    if n[k] >= max_deaths:
                        nxt[k] = float("inf")
                        continue
                    ps = pick("youngadult,adult,elder", 40)
                    if not ps:
                        say("no townie to kill; retry in 2 min")
                        nxt[k] = time.time() + 120
                        continue
                    r = ag("kill_sim", {"sim": f"picked:{rng.randint(1, len(ps))}", "death": DEATHS[n[k] % len(DEATHS)]})
                    if r.get("ok"):
                        n[k] += 1
                        res = r.get("result") or {}
                        say(f"death {n[k]}: {res.get('sim')} ({res.get('death')}, killed {res.get('killed')})")
                    else:
                        say(f"kill failed: {err(r)}")
                elif k == "occult":
                    ps = pick("teen,youngadult,adult", 40)
                    if ps:
                        t = OCCULTS[n[k] % len(OCCULTS)]
                        r = ag("make_occult", {"sim": f"picked:{rng.randint(1, len(ps))}", "type": t}, 15000)
                        say(f"occult {t}: {json.dumps(r.get('result'))[:140] if r.get('ok') else 'failed ' + err(r)}")
                        n[k] += 1
                elif k == "age":
                    ps = pick("child,teen,youngadult,adult", 40)
                    if ps:
                        r = ag("age_up", {"sim": f"picked:{rng.randint(1, len(ps))}"}, 15000)
                        say(f"age up: {json.dumps(r.get('result'))[:140] if r.get('ok') else 'failed ' + err(r)}")
                        n[k] += 1
                elif k == "job":
                    ps = pick("youngadult,adult", 40)
                    for i in rng.sample(range(1, len(ps) + 1), len(ps)):
                        r = ag("career", {"sim": f"picked:{i}", "action": "quit"})
                        if r.get("ok"):
                            say(f"job lost: {json.dumps(r.get('result'))[:140]}")
                            n[k] += 1
                            break
                elif k == "promote":
                    names = pick("youngadult,adult,elder", 40)
                    for i in rng.sample(range(1, len(names) + 1), min(3, len(names))):
                        act = "demote" if n[k] % 4 == 3 else "promote"
                        r = ag("career", {"sim": f"picked:{i}", "action": act})
                        if not r.get("ok") and "no job" in err(r):
                            o = ag("career", {"sim": f"picked:{i}", "action": "options"})
                            res = o.get("result")  # career options answers a list of {id, name, available} (al339 crashed on .get)
                            opts = [x.get("id") for x in (res if isinstance(res, list) else (res or {}).get("careers") or []) if isinstance(x, dict) and x.get("available")]
                            if opts:
                                j = ag("career", {"sim": f"picked:{i}", "action": "join", "career": rng.choice(opts)})
                                say(f"job joined: {json.dumps(j.get('result'))[:120] if j.get('ok') else 'failed ' + err(j)}")
                                r = ag("career", {"sim": f"picked:{i}", "action": act})
                        if r.get("ok"):
                            n["promotions"] = n.get("promotions", 0) + 1
                        elif fails[k] < 12:
                            fails[k] += 1
                            say(f"{act} failed: {err(r)}")
                    # the whole town up the ladder like story progression (EA PromoteSim, next townies by id, wraps)
                    r = ag("career_ladder", {"count": 20}, 20000)
                    res = r.get("result") if r.get("ok") else None
                    if isinstance(res, dict):
                        up = res.get("promoted") or []
                        n["ladder_promoted"] = n.get("ladder_promoted", 0) + sum(1 for x in up if isinstance(x, dict) and not x.get("error"))
                        say(f"career ladder: {len(up)} promoted, eligible {res.get('eligible')}, at top {res.get('at_top')},"
                            f" jobless {res.get('jobless')}{', wrapped' if res.get('wrapped') else ''}")
                    elif fails[k] < 12:
                        fails[k] += 1
                        say(f"career ladder failed: {err(r)}")
                    n[k] += 1
                elif k == "party":
                    r = ag("throw_party", {"count": 12, "hours": 0.5}, 20000)
                    say(f"party: {json.dumps(r.get('result'))[:160] if r.get('ok') else 'failed ' + err(r)}")
                    n[k] += 1
                elif k == "crowd":
                    lot = LOTS[n[k] % len(LOTS)]
                    names = pick("teen,youngadult,adult,elder", 40)
                    went = 0
                    for i in rng.sample(range(1, len(names) + 1), min(20, len(names))):
                        r = ag("sim_visit_lot", {"sim": f"picked:{i}", "lot": lot})
                        if r.get("ok"):
                            went += 1
                        elif fails[k] < 12:
                            fails[k] += 1
                            say(f"crowd visit failed: {err(r)}")
                    say(f"crowd {n[k] + 1} to {lot}: {went} townies sent")
                    n[k] += 1
                elif k == "skill":
                    names = pick("child,teen,youngadult,adult,elder", 40)
                    for i in rng.sample(range(1, len(names) + 1), min(3, len(names))):
                        r = ag("set_skill", {"sim": f"picked:{i}", "skill": rng.choice(SKILLS), "level": rng.randint(1, 10)})
                        if r.get("ok"):
                            n["skills_set"] = n.get("skills_set", 0) + 1
                        elif fails[k] < 12:
                            fails[k] += 1
                            say(f"skill failed: {err(r)}")
                    n[k] += 1
                elif k == "lifecycle":
                    if doomed is None:
                        names = pick("child,teen", 40) or pick("youngadult,adult", 40)
                        if not names:
                            continue
                        doomed = rng.choice(names)
                        say(f"lifecycle: {doomed} will age up every 6 min until old age")
                    d = ag("sim_details", {"sim": doomed})
                    age = str((d.get("result") or {}).get("age", "")) if d.get("ok") else ""
                    r = ag("age_up", {"sim": doomed, "confirm_elder_death": True}, 20000)
                    say(f"lifecycle {doomed} ({age or 'gone'}): {json.dumps(r.get('result'))[:120] if r.get('ok') else 'failed ' + err(r)}")
                    if not r.get("ok") or age.lower() == "elder":
                        doomed = None  # dead (or gone): next round picks a new one
                    n[k] += 1
                elif k == "fight":
                    names = pick("youngadult,adult,elder", 40)
                    idx = rng.sample(range(1, len(names) + 1), min(8, len(names)))
                    fought, lab = 0, None
                    for i in range(0, len(idx) - 1, 2):
                        a_, b_ = f"picked:{idx[i]}", f"picked:{idx[i + 1]}"
                        ag("modify_relationship", {"sim": a_, "other": b_, "state": "Enemy", "liking": -100})
                        lab = "Force Fight!"  # EA's own forced fight (SocialCallback pushes it): plain "Fight!" fails its test for townie pairs
                        q, r = social(a_, b_, lab)
                        if q:
                            fought += 1
                        elif fails[k] < 12:
                            fails[k] += 1
                            say(f"fight failed: {err(r) if not r.get('ok') else 'queue refused it'}")
                    n[k] += 1
                    n["fights_queued"] = n.get("fights_queued", 0) + fought
                    say(f"fight round {n[k]}: {fought} fights queued among {len(idx)} townies{f' ({lab})' if lab else ''}")
                    # "every Sim fights": nearest pairs made Enemies, a fight from their own Mean menu
                    social_round({"pairs": 40, "cats": "mean", "query": "fight", "enemies": True, "max_dist": 60}, "fight", n, fails)
                elif k == "outfit":
                    r = ag("outfit_change", {"count": 10, "townies": True, "category": OUTFITS[n[k] % len(OUTFITS)]}, 15000)
                    if r.get("ok"):
                        n[k] += 1
                        n["outfits_changed"] = n.get("outfits_changed", 0) + len((r.get("result") or {}).get("changed") or [])
                    elif fails[k] < 12:
                        fails[k] += 1
                        say(f"outfit change failed: {err(r)}")
                    # CAS-style rebuilds without CAS (OutfitStressHandlers.cs): the tattoo chair's failure on every outfit,
                    # the stylist's makeover (bad and good rounds); each answers the previous call's counts in "last"
                    for cmd, args in (("bad_tattoo", {"count": 10}), ("bad_makeover", {"count": 5, "kind": "bad" if n[k] % 2 else "good"})):
                        r = ag(cmd, args, 20000)
                        if r.get("ok"):
                            res = r.get("result") or {}
                            v = res.get("tattooed") or res.get("queued") or 0  # bad_makeover's "queued" is a list of names (al343 died on it)
                            n[cmd] = n.get(cmd, 0) + (len(v) if isinstance(v, list) else int(v))
                            say(f"{cmd} {args.get('kind', '')}: {json.dumps(res)[:200]}")
                        elif fails[k] < 12:
                            fails[k] += 1
                            say(f"{cmd} failed: {err(r)}")
                elif k == "pregnancy":
                    if mother:
                        r = ag("pregnancy_advance", {"sim": mother, "to": "birth"}, 30000)
                        say(f"birth {mother}: {json.dumps(r.get('result'))[:160] if r.get('ok') else 'failed ' + err(r)}")
                        mother = None
                        n[k] += 1
                        continue
                    names = pick("youngadult,adult", 40)
                    fems, male = [], None
                    for i in rng.sample(range(1, len(names) + 1), len(names)):
                        d = ag("sim_details", {"sim": f"picked:{i}"})
                        g = str(((d.get("result") or {}).get("gender")) if d.get("ok") else "").lower()
                        if g.startswith("f") and len(fems) < 5:
                            fems.append(names[i - 1])
                        elif g.startswith("m") and not male:
                            male = names[i - 1]
                        if len(fems) >= 5 and male:
                            break
                    if not (fems and male):
                        say("pregnancy: no female + male townie pair found")
                        continue
                    # the game refuses many townies (no home lot, full household): try up to 5 (al337 tried 1 and failed)
                    for fem in fems:
                        r = ag("pregnancy_advance", {"sim": fem, "partner": male, "to": "pregnant"}, 30000)
                        if r.get("ok"):
                            mother = fem
                            break
                    say(f"pregnant {fem} by {male}: {json.dumps(r.get('result'))[:160] if r.get('ok') else 'failed (' + str(len(fems)) + ' tried) ' + err(r)}")
                elif k in ("adopt", "giveaway"):
                    r = ag("pet_adopt", {"count": 3}, 30000) if k == "adopt" else ag("pet_give_away", {"count": 2}, 30000)
                    say(f"{k} {n[k] + 1}: {json.dumps(r.get('result'))[:220] if r.get('ok') else 'failed ' + err(r)}")
                    n[k] += 1
                elif k == "season":
                    # the world changes season (test_env fixed: forced season, sunny 48 h, moon first quarter); never winter,
                    # where cold could freeze the active household outdoors
                    r = ag("test_env", {"mode": "fixed", "season": SEASONS[n[k] % len(SEASONS)], "weather_hours": 48}, 20000)
                    say(f"season {n[k] + 1}: {json.dumps(r.get('result'))[:160] if r.get('ok') else 'failed ' + err(r)}")
                    n[k] += 1
                elif k == "traffic":
                    r = ag("spawn_traffic", {"count": 10}, 20000)
                    if n[k] % 6 == 0 or not r.get("ok"):
                        say(f"traffic {n[k] + 1}: {json.dumps(r.get('result'))[:160] if r.get('ok') else 'failed ' + err(r)}")
                    n[k] += 1
                elif k == "move":
                    r = ag("move_household", {}, 30000)
                    say(f"move {n[k] + 1}: {json.dumps(r.get('result'))[:160] if r.get('ok') else 'failed ' + err(r)}")
                    n[k] += 1
                elif k == "story":
                    # spam (user 11-09, "crank it up"): EA story progression Accelerated with 60 actions per story day; the
                    # calls alternate 2 story days (EA's cooldowns) and a 60-action burst without the category cooldowns
                    # (story_burst); either is skipped while the last batch still runs
                    if n[k] % 2:
                        r = ag("story_burst", {"actions": 60}, 20000)
                    else:
                        r = ag("story_days", {"days": 2, "mode": "Accelerated", "actions": 60} if n[k] == 0 else {"days": 2}, 20000)
                    if (r.get("result") or {}).get("busy"):
                        n["story_busy"] = n.get("story_busy", 0) + 1
                    if n[k] % 10 == 0 or not r.get("ok"):
                        say(f"story {n[k] + 1}: {json.dumps(r.get('result'))[:160] if r.get('ok') else 'failed ' + err(r)}")
                    if n[k] % 10 == 9:
                        st = ag("story_burst_state", {})
                        say(f"story burst state: {json.dumps(st.get('result'))[:400]}")
                    if n[k] % 10 == 0:
                        lg = ag("async_log", {})
                        say(f"async log: {json.dumps(lg.get('result'))[-300:]}")
                    n[k] += 1
                elif k == "spawn":
                    if remove_at is None:
                        r = ag("spawn_sims", {"count": 6}, 30000)
                        say(f"movers spawned: {json.dumps(r.get('result'))[:160] if r.get('ok') else 'failed ' + err(r)}")
                        remove_at = time.time() + 15 * 60
                        n[k] += 1
        except Busy as e:  # raised before the command went out
            if snap is not None:
                nxt = snap
            say(f"busy file back before {e}: batch stopped, {k if snap is not None else 'no event'} and the rest stay due")
    ARMED = False  # the run is over (or the game gone): no mode cycle left to wait for
    r = ag("patches")
    if r.get("ok"):
        say("counters at end: " + " ".join(f"{k}={r['result'].get(k)}" for k in COUNTERS))
    say("stress ended: " + " ".join(f"{k}={v}" for k, v in n.items()))


def self_check():
    """--self-check: the busy-file wait, the command gate and one run whose batch the busy file stops, in a temp dir with a
    fake agent (urllib's urlopen replaced, game_up forced): no game, no network"""
    global BUSY, ARMED, game_up
    import contextlib
    import io
    import shutil
    import tempfile
    import threading
    d = tempfile.mkdtemp(prefix="stress_m_check_")
    busy, out, start = (os.path.join(d, f) for f in ("t-busy", "t.out", "t.start"))
    flag = busy + ".stress"

    def took(f, *x, **kw):
        t = time.time()
        return f(*x, **kw), time.time() - t
    # 1. the wait: no file; a file gone after 1.5 s; a file that stays (max_s 2)
    r, s = took(wait_free, busy)
    assert r and s < 1, (r, s)
    open(busy, "w").close()
    threading.Timer(1.5, os.remove, [busy]).start()
    r, s = took(wait_free, busy)
    assert r and 1.3 < s < 5, (r, s)
    open(busy, "w").close()
    r, s = took(wait_free, busy, max_s=2)
    assert not r and 2 <= s < 4, (r, s)
    calls = []

    def fake(url, timeout=None):
        cmd = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)["cmd"][0]
        assert not BUSY or os.path.exists(flag), f"{cmd} went out without {flag}"
        assert not (ARMED and os.path.exists(busy)), f"{cmd} went out while the busy file was there"
        calls.append(cmd)
        res = {"game_state": {"sub_state": "LiveModeState"}, "pick_sims": {"picked": [f"Sim {i}" for i in range(1, 9)]},
               "sim_details": {"gender": "Female" if calls.count(cmd) % 2 else "Male"}}.get(cmd, {})
        if cmd == "social" and calls.count(cmd) == 1:  # the modes side takes the lock mid-batch, frees it 1 s later
            open(busy, "w").close()
            threading.Timer(1, os.remove, [busy]).start()
        if cmd == "pregnancy_advance":  # the batch's last event: end the run
            with open(out, "w") as f:
                f.write("t done\n")
        return io.BytesIO(json.dumps({"ok": True, "result": res}).encode())
    urllib.request.urlopen = fake
    # 2. the gate: armed + file there = Busy, nothing sent, flag gone; not armed (stale file) = sent; no --busy-file = no flag
    BUSY, ARMED = busy, True
    try:
        ag("x")
        raise AssertionError("no Busy with the busy file there")
    except Busy:
        pass
    assert calls == [] and not os.path.exists(flag), calls
    ARMED = False
    assert ag("x")["ok"] and calls == ["x"] and not os.path.exists(flag), calls
    os.remove(busy)
    BUSY = None
    assert ag("y")["ok"] and calls == ["x", "y"] and not os.path.exists(flag), calls
    # 3. a run with every event due at once; the busy file appears at the first social, goes 1 s later
    del calls[:]
    open(start, "w").close()
    os.utime(start, (time.time() - 3600,) * 2)
    game_up = lambda: True
    sys.argv = ["leak-stress-drip.py", start, out, "--start", "0", "--busy-file", busy]
    log = io.StringIO()
    with contextlib.redirect_stdout(log):
        main()
    try:
        i = calls.index("social")
        assert calls[i + 1] == "game_state", calls[i:i + 3]  # stopped: the next command is the next batch's live check
        assert calls.count("game_state") == 2 and calls.count("social") == 1 + 4 + 4, calls
        assert "fight round 1: 4 fights queued among 8 townies (Force Fight!)" in log.getvalue()
        assert calls.count("social_round") == 2 and "social_round fight: 0 queued" in log.getvalue(), calls
        assert [calls.count(c) for c in ("kill_sim", "make_celebrity", "spawn_sims", "pregnancy_advance")] == [1, 1, 1, 1], calls
        assert [calls.count(c) for c in ("test_env", "spawn_traffic", "story_days")] == [1, 1, 1], calls
        assert calls[-1] == "patches" and not os.path.exists(flag) and not os.path.exists(busy)
        assert "busy file back before social: batch stopped, social and the rest stay due" in log.getvalue()
        assert log.getvalue().count("batch stopped") == 1  # the next batch waited for the file instead of hitting it again
        assert "stress ended:" in log.getvalue()
    except AssertionError:
        print(log.getvalue())
        raise
    shutil.rmtree(d)
    print(f"self-check ok: wait (gone / 1.5 s / stale 2 s), gate (Busy, stale, no file), run ({len(calls)} fake commands, "
          f"batch stopped at social and rerun, each event once, 4 fights)")


if __name__ == "__main__":
    main()
