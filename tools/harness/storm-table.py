#!/usr/bin/env python3
# storm-table.py (docs/668): compact per-arm table of route_storm / idle runs from phasesoakr run dirs.
#   python3 -I tools/harness/storm-table.py [--min-ms 150] [--report route-stutter-report.md] [label=]<run dir>...
# --report: the hitch rows of tools/harness/route-stutter-report.ts --hitches (| log | at | ms | gc | cause | caller ...) are
# put into the phase their `at` falls in and printed as one cause line per hitch under the table.
# A dir's log (ts3native-full.log, else ts3native.log) is split into phases at the harness's `mark ev=p-<tag>` lines (snap
# base, then one per phase end); each phase gets: game hitches >= min ms (ts3fixes `hitchwatch: N ms ... agent -`) count / ms,
# slow plans (`ufplan:` lines; >= 300 ms), plans and fails per minute from the cumulative `route plans: ... on N (fails F)`
# lines over the `at T ms` clock, route_storm waves / pushes / skips, the proof lines of the opt-in kinds (nl/fgl/fgln/s:
# nearestlot, fgl prof search, fglnonescan, stray helper), `hitch: frame ... cause C` classes, and the last fix-counter
# lines in the phase (nopath memo, route speed, height cache, KindsUnseen). `host` = the ms of the `host N/M` column of the
# `route candidate split` lines (the managed door asks under the planner, docs/667); `mism` = NeighbourMemo / FloodMemo
# shadow mismatches: the largest `mismatch N` of the phase's `route speed: neighbour memo` / `flood memo` minute lines plus
# the NEIGHBOUR / FLOOD MEMO MISMATCH lines (must be 0 before serving).
import os
import re
import sys
from collections import Counter

COUNTERS = [('npm-fuzzy', re.compile(r'route nopath memo: fuzzy \d+ household')),
            ('npm', re.compile(r'^route nopath memo: (?!fuzzy)')),
            ('speed-nm', re.compile(r'^route speed: neighbour memo')),
            ('speed-fm', re.compile(r'^route speed: flood memo')),
            ('speed', re.compile(r'^route speed (?!: )')),
            ('height', re.compile(r'^height cache')),
            ('kunseen', re.compile(r'KindsUnseen: own kind')),
            ('switch', re.compile(r'ts3fixes switches npmfuzzy'))]
AT = re.compile(r'\bat (\d+) ms')
HW = re.compile(r'hitchwatch: (\d+) ms, .*agent (\S+),')
PL = re.compile(r'route plans: off \d+ \(fails \d+\) on (\d+) \(fails (\d+)\)')
UF = re.compile(r'ts3fixes: ufplan: kind (\d+) (\d+) ms')
HF = re.compile(r'hitch: frame (\d+) ms .*?cause (\S+?):')
MK = re.compile(r'diag: mem #\d+ mark ev=p-(\S+)')
WV = re.compile(r'route_storm: wave \d+ pushed (\d+)')
SK = re.compile(r'route_storm: wave \d+ pushed .*?, skipped (\d+)')
# lines that prove an opt-in storm kind reached its fix (docs/668 §1.1): taxi, jam / laptop / putdown, stray
PROOF = [('nl', re.compile(r'ts3fixes: nearestlot:')), ('fgl', re.compile(r'fgl prof: search ')),
         ('fgln', re.compile(r'ts3fixes: fglnonescan:')), ('stray', re.compile(r'route_storm: stray helper called'))]
HS = re.compile(r'route candidate split: .*?, host (\d+)/(\d+)')
MM = re.compile(r'^route speed: (neighbour|flood) memo.*?\bmismatch(?:es)?[ =:]+(\d+)')
ML = re.compile(r'(NEIGHBOUR|FLOOD) MEMO MISMATCH')


def phase_names(d):
    names = []
    try:
        with open(os.path.join(d, 'phase.txt'), errors='replace') as f:
            for line in f:
                m = re.search(r'\] phase (\S+) \((\d+) min\)', line)
                if m:
                    names.append(m.group(1) + ':' + m.group(2))
    except OSError:
        pass
    return names


def new_seg(tag):
    return {'tag': tag, 'hn': 0, 'hms': 0, 'h500': 0, 'uf': 0, 'uf300': 0, 'p0': None, 'p1': None, 't0': None,
            't1': None, 'waves': 0, 'pushed': 0, 'skipped': 0,
            'proof': Counter(), 'cause': Counter(), 'ctr': {}, 'host': 0, 'hostn': 0,
            'mm': {'neighbour': 0, 'flood': 0}, 'mml': 0, 'mmseen': False}


def scan(d):
    log = os.path.join(d, 'ts3native-full.log')
    if not os.path.exists(log):
        log = os.path.join(d, 'ts3native.log')
    segs, cur, at = [], None, None
    with open(log, errors='replace') as f:
        for line in f:
            m = AT.search(line) if line.startswith('ts3fixes:') else None
            if m and int(m.group(1)) > 1000000:  # the clock of ts3fixes lines (other `at N ms` are durations)
                at = int(m.group(1))
                if cur is not None:
                    if cur['t0'] is None:
                        cur['t0'] = at
                    cur['t1'] = at
            m = MK.search(line)
            if m:
                if cur is not None:
                    segs.append(cur)
                cur = new_seg(m.group(1))
                continue
            if cur is None:
                continue
            m = HW.search(line)
            if m and m.group(2) == '-' and int(m.group(1)) >= MIN:
                v = int(m.group(1))
                cur['hn'] += 1
                cur['hms'] += v
                cur['h500'] += v >= 500
            m = PL.search(line)
            if m:
                p = (int(m.group(1)), int(m.group(2)), at)
                if cur['p0'] is None:
                    cur['p0'] = p
                cur['p1'] = p
            m = UF.search(line)
            if m:
                cur['uf'] += 1
                cur['uf300'] += int(m.group(2)) >= 300
            m = HF.search(line)
            if m and int(m.group(1)) >= MIN:
                cur['cause'][m.group(2)] += 1
            m = HS.search(line)
            if m:
                cur['host'] += int(m.group(1))
                cur['hostn'] += 1
            m = MM.search(line)
            if m:
                k = m.group(1)
                cur['mm'][k] = max(cur['mm'][k], int(m.group(2)))
                cur['mmseen'] = True
            if ML.search(line):
                cur['mml'] += 1
                cur['mmseen'] = True
            m = WV.search(line)
            if m:
                cur['waves'] += 1
                cur['pushed'] += int(m.group(1))
            m = SK.search(line)
            if m:
                cur['skipped'] += int(m.group(1))
            for k, rx in PROOF:
                if rx.search(line):
                    cur['proof'][k] += 1
            for k, rx in COUNTERS:
                if rx.search(line):
                    cur['ctr'][k] = line.strip()[:220]
    # the segment opened by the base mark is phase 1, the one opened by phase 1's end mark is phase 2, ...; the segment after
    # the last phase's mark (end snap, quit) is dropped
    names = phase_names(d)
    out = []
    for i, s in enumerate(segs):
        if i >= len(names):
            break
        s['phase'] = names[i]
        out.append(s)
    return out


def row(label, s):
    mins = (s['t1'] - s['t0']) / 60000 if s['t0'] and s['t1'] and s['t1'] > s['t0'] else 0
    ppm = fpm = '-'
    if s['p0'] and s['p1'] and s['p0'][2] and s['p1'][2] and s['p1'][2] > s['p0'][2]:
        dm = (s['p1'][2] - s['p0'][2]) / 60000
        ppm = f"{(s['p1'][0] - s['p0'][0]) / dm:.0f}"
        fpm = f"{(s['p1'][1] - s['p0'][1]) / dm:.1f}"
    causes = ' '.join(f'{k} {v}' for k, v in s['cause'].most_common(5)) or '-'
    hit = f"{s['hn']} / {s['hms']}"
    host = f"{s['host']}" if s['hostn'] else '-'
    mism = f"{s['mm']['neighbour']}/{s['mm']['flood']}/{s['mml']}" if s['mmseen'] else '-'
    proof = '/'.join(str(s['proof'][k]) for k, _ in PROOF)
    return (f"{label[:22]:22} {s['phase'][:10]:10} {mins:5.1f} {hit:>15} {s['h500']:5} {s['uf']:6} {s['uf300']:5} "
            f"{ppm:>9} {fpm:>9} {s['waves']:5} {s['pushed']:6} {s['skipped']:5} {proof:>13} {host:>7} {mism:>8}  {causes}")


def report_rows(path):
    rows = []
    try:
        with open(path, errors='replace') as f:
            for line in f:
                c = [x.strip() for x in line.split('|')]
                if len(c) > 7 and c[2].isdigit() and c[3].isdigit():
                    rows.append((c[1], int(c[2]), int(c[3]), c[5], c[6]))
    except OSError:
        pass
    return rows


def main(args, report=None):
    rows = report_rows(report) if report else []
    causes = []
    print(f"{'arm':22} {'phase':10} {'min':>5} {'hitch n / ms':>15} {'>=500':>5} {'slowpl':>6} {'>=300':>5} "
          f"{'plans/min':>9} {'fails/min':>9} {'waves':>5} {'pushed':>6} {'skip':>5} {'nl/fgl/fgln/s':>13} {'host ms':>7} {'mism n/f/l':>8}  frame causes")
    details = []
    for a in args:
        if '=' in a and not os.path.isdir(a):
            label, d = a.split('=', 1)
        else:
            label, d = os.path.basename(a.rstrip('/')), a
        try:
            segs = scan(d)
        except OSError as e:
            print(f'{label}: {e}')
            continue
        for s in segs:
            print(row(label, s))
            for log, at, ms, cause, caller in rows:
                if d.rstrip('/') in log and s['t0'] and s['t1'] and s['t0'] <= at <= s['t1'] + 5000:
                    causes.append(f"  {label[:22]} {s['phase']}: {ms} ms {cause}: {caller[:120]}")
            if s['ctr']:
                details.append((label, s['phase'], s['ctr']))
    if causes:
        print('\nper-hitch causes (route-stutter-report rows by phase):')
        print('\n'.join(causes))
    if details:
        print('\nfix counters (last line per phase):')
        for label, ph, c in details:
            for v in c.values():
                print(f'  {label[:22]} {ph}: {v}')


MIN = 150
if __name__ == '__main__':
    argv = sys.argv[1:]
    if argv[:1] == ['--min-ms']:
        MIN = int(argv[1])
        argv = argv[2:]
    rep = None
    if argv[:1] == ['--report']:
        rep = argv[1]
        argv = argv[2:]
    main(argv, rep)
