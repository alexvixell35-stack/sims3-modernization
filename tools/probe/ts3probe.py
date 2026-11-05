"""Live inspection server for TS3W.exe (The Sims 3).

Python, not TypeScript, because Frida's Python bindings are already installed (pip frida-tools);
the Node binding is a native npm module. Standard library otherwise.

  python tools/probe/ts3probe.py serve [--launch [--env NAME=VALUE ...]] [--world X.sims3] [--profile] [--port 47891]
  python tools/probe/ts3probe.py get status|threads|memory|rescache|modules|events|scripterrors
  python tools/probe/ts3probe.py get "read?addr=0xd81840&len=32"
  python tools/probe/ts3probe.py get "agent?cmd=ping"        (in-game ts3agent mod, see mods/ts3agent)
  python tools/probe/ts3probe.py get detach                  (always stop the server this way, not by killing it)
  python tools/probe/ts3probe.py get reload                  (reload tools/probe/agent.js in place)

serve attaches to a running TS3W.exe (or starts it with --launch), loads tools/probe/agent.js
(and tools/frida/stutter-profile.js with --profile), serves JSON on 127.0.0.1 only, and appends
every event (native exceptions, script errors, detach/crash) to probe-out/<timestamp>/events.jsonl.
Env: TS3_EXE process name (default TS3W.exe); TS3_LAUNCH=ea starts EALauncher.exe (TS3_EA_LAUNCHER, TS3_EA_URI), no game args, and sets LoadOption.ini selectedflags from the launcher's LastRunIProducts & LastRunEProducts (TS3_SELECTED_FLAGS overrides; docs/234); the startup-popup answerer runs only then or with TS3_ANSWER_STARTUP_DIALOG=1.
"""
import argparse, bisect, csv, glob, json, os, re, subprocess, sys, threading, time, urllib.error, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

frida = None  # imported in serve only, so `get` runs without it

ROOT = Path(__file__).resolve().parents[2]
GAME_BIN = Path(os.environ.get('TS3_BIN', r'C:\Program Files (x86)\Steam\steamapps\common\The Sims 3\Game\Bin'))
GAME_EXE = os.environ.get('TS3_EXE', 'TS3W.exe')
# ts3agent commands that may leave live mode or change the household on purpose. /status reports the last one sent
# (state_cmd, state_cmd_ago_s), so the harness tells a requested mode exit from one a dialog click caused (docs/236).
# auto_dialog is not one: it answers a dialog nobody asked for.
STATE_CMD = re.compile(r'^(build_mode|cas_|buy_open|buy_close|save_game|saver_like_save|travel_|load_world|load_save|play_|quit_game|'
                       r'main_menu|enter_live_mode|make_household|switch_household|picker_|enter_play_flow|fresh_world|uni_move_in|'
                       r'et_move_lot|houseboat_move|respond_dialog|reflect_call|kill_sim|age_up|make_sim)')
# Native start-up warning "unofficial game modification that does not match your current version" (docs/231): a Win32
# dialog (template 104 in the exe: buttons 1001 Quit, 1004 Continue, static 1003) shown before the script VM runs, so the
# in-game agent cannot answer it. The probe posts the Continue click (no focus, nothing else changes).
MOD_VERSION_DIALOG = {'quit': 1001, 'continue': 1004, 'text': 1003}

def is_mod_version_dialog(class_name, control_ids):
    """Only the exe's own warning template: dialog class and all three control ids present."""
    return class_name == '#32770' and set(MOD_VERSION_DIALOG.values()) <= set(control_ids)

USER_DIR = Path(os.environ.get('TS3_USER', Path.home() / 'Documents' / 'Electronic Arts' / 'The Sims 3'))
FUNCTIONS_CSV = ROOT / 'game-copy' / 'analysis' / 'functions.csv'


def next_misses(misses, present):
    """Progress check of a long wait (docs/236 #18): a miss counts up, a hit resets; two in a row end the wait."""
    return 0 if present else misses + 1


class Symbols:
    """Maps addresses to 'TS3W.exe!Name+0x12' (Ghidra export) or 'module+0x1234'."""

    def __init__(self, script):
        self.script = script
        self.starts, self.ends, self.names = [], [], []
        if FUNCTIONS_CSV.exists():
            with open(FUNCTIONS_CSV, newline='') as f:
                for row in csv.DictReader(f):
                    self.starts.append(int(row['entry'], 16)); self.ends.append(int(row['end'], 16)); self.names.append(row['name'])
        self.modules = []
        self.refresh_modules()

    def refresh_modules(self):
        self.modules = sorted((int(m['base'], 16), int(m['base'], 16) + m['size'], m['name']) for m in self.script.exports_sync.modules())

    def name(self, addr_text):
        try:
            a = int(addr_text, 16)
        except (TypeError, ValueError):
            return addr_text
        mod = next((m for m in self.modules if m[0] <= a < m[1]), None)
        if mod is None:  # a DLL loaded after attach: refresh once
            self.refresh_modules()
            mod = next((m for m in self.modules if m[0] <= a < m[1]), None)
        if mod and mod[2].lower() == 'ts3w.exe' and self.starts:
            i = bisect.bisect_right(self.starts, a) - 1
            if i >= 0 and a <= self.ends[i]:
                return f'{addr_text} TS3W.exe!{self.names[i]}+0x{a - self.starts[i]:x}'
        if mod:
            return f'{addr_text} {mod[2]}+0x{a - mod[0]:x}'
        return f'{addr_text} ?'

    def stack(self, frames):
        return [self.name(f) for f in frames]


class Probe:
    def __init__(self, args):
        self.args = args
        self.out = ROOT / 'probe-out' / time.strftime('%Y%m%d-%H%M%S')
        self.out.mkdir(parents=True, exist_ok=True)
        self.events_file = open(self.out / 'events.jsonl', 'a', encoding='utf-8')
        self.lock = threading.Lock()
        self.agent_lock = threading.Lock()
        self.state_cmd = None  # (cmd, time.time()) of the last STATE_CMD sent
        # one stack sample in flight: a sampler whose client timed out must not queue more calls in front of agentstatus
        # (the queued backlog read as a dead game, 2026-10-16-sampler-hang.md)
        self.sample_lock = threading.Lock()
        self.script_errors = []
        self.alive = True

    def log(self, event):
        with self.lock:
            if 'stack' in event and hasattr(self, 'symbols'):
                event['stack'] = self.symbols.stack(event['stack'])
            self.events_file.write(json.dumps(event) + '\n')
            self.events_file.flush()
        if event.get('kind') != 'note':
            print(f"[ts3probe] {event.get('kind')}: {event.get('type') or event.get('file') or event.get('reason', '')}", flush=True)

    def launch(self):
        ea = os.environ.get('TS3_LAUNCH', 'direct') == 'ea'
        line, why = check_session()
        print(f'[ts3probe] {line}', flush=True)
        if why:
            raise SystemExit(f'[ts3probe] {why}; launch refused')
        ensure_audio_off(USER_DIR / 'Options.ini')  # every launch, vanilla or not (docs/236 round 4)
        if ea:  # EA app edition: only EALauncher.exe with the offer URI works (a direct start is bounced by the DRM)
            ensure_selected_flags(USER_DIR / 'LoadOption.ini')
            log_ea_processes()
            cmd = [os.environ.get('TS3_EA_LAUNCHER', r'C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EALauncher.exe'),
                   os.environ.get('TS3_EA_URI', 'origin2://game/launch/?offerIds=sims3_dd,1002441,70384_CE,70384_Unrevised')]
            if self.args.world or self.args.extra:
                print('[ts3probe] WARN: TS3_LAUNCH=ea cannot pass game arguments; --world and extra arguments are ignored', flush=True)
        else:
            cmd = [str(GAME_BIN / GAME_EXE)]
            if self.args.world:
                cmd.append(f'-worldFile:{self.args.world}')
            cmd += self.args.extra or []
        print(f'[ts3probe] launching {cmd}', flush=True)
        env = dict(os.environ, **dict(e.split('=', 1) for e in self.args.env))
        if self.args.env:
            print(f'[ts3probe] game environment: {self.args.env}', flush=True)
        self.proc = subprocess.Popen(cmd, cwd=None if ea else GAME_BIN, env=env)
        for _ in range(120):  # the exe may hand over to another instance; wait for the process by name
            try:
                session = frida.attach(GAME_EXE)
                try: self.game_pid = frida.get_local_device().get_process(GAME_EXE).pid
                except Exception: self.game_pid = None
                return session
            except frida.ProcessNotFoundError:
                time.sleep(0.5)
        raise SystemExit(f'{GAME_EXE} did not start within 60 s')

    def start(self):
        self.session = self.launch() if self.args.launch else frida.attach(GAME_EXE)
        self.session.on('detached', self.on_detached)
        self.script = self.session.create_script((ROOT / 'tools' / 'probe' / 'agent.js').read_text(encoding='utf-8'))
        self.script.on('message', self.on_message)
        self.script.load()
        self.symbols = Symbols(self.script)
        if self.args.exception_stacks:
            self.script.exports_sync.enable_exceptions()
        if self.args.profile:
            prof = self.session.create_script((ROOT / 'tools' / 'frida' / 'stutter-profile.js').read_text(encoding='utf-8'))
            prof.on('message', lambda m, d: print('[profile]', m.get('payload', m), flush=True))
            prof.load()
            prof.exports_sync.init('late', {'out': str(self.out / 'profile').replace('\\', '/')})
        threading.Thread(target=self.watch_script_errors, daemon=True).start()
        if os.environ.get('TS3_LAUNCH') == 'ea' or os.environ.get('TS3_ANSWER_STARTUP_DIALOG') == '1':
            threading.Thread(target=self.answer_startup_dialog, daemon=True).start()
        else:
            print('[ts3probe] startup-dialog answerer skipped (TS3_LAUNCH != ea and TS3_ANSWER_STARTUP_DIALOG != 1)', flush=True)
        threading.Thread(target=self.sample_memory, daemon=True).start()
        if sys.platform == 'win32':
            threading.Thread(target=self.keep_window_place, daemon=True).start()
        print(f'[ts3probe] attached; serving http://127.0.0.1:{self.args.port}; events in {self.out}', flush=True)

    def on_message(self, message, data):
        if message['type'] == 'send' and message['payload'].get('type') == 'event':
            self.log(message['payload']['event'])
        elif message['type'] == 'send' and message['payload'].get('type') == 'taskstall':  # one line per stage stall (docs/04 F17)
            with self.lock, open(self.out.parent / f'{self.out.name}-taskstall.jsonl', 'a', encoding='utf-8') as f:
                f.write(json.dumps(message['payload']['stall']) + '\n')
        elif message['type'] == 'error':
            self.log({'kind': 'agent-error', 'description': message.get('description'), 'stack': []})

    def on_detached(self, reason, crash):
        info = {'kind': 'detached', 'reason': reason, 'time': time.strftime('%Y-%m-%dT%H:%M:%S')}
        if crash is not None:
            info['crash'] = {'summary': crash.summary, 'report': crash.report}
        proc = getattr(self, 'proc', None)
        if reason == 'process-terminated' and proc is not None and proc.pid == getattr(self, 'game_pid', None):
            # the game's own exit status: a stack overflow or fast fail ends TS3W with no xcpt report (crash-ledger triage reads it)
            info['pid'] = proc.pid
            try: info['exitCode'] = '0x%08X' % (proc.wait(timeout=10) & 0xFFFFFFFF)
            except subprocess.TimeoutExpired: pass
        self.log(info)
        self.alive = False

    def answer_startup_dialog(self):
        """Clicks Continue on the mod-version warning (MOD_VERSION_DIALOG) for up to 5 min after start, polling once a second."""
        import ctypes
        from ctypes import wintypes
        u = ctypes.windll.user32
        u.GetDlgItem.restype = wintypes.HWND
        pid = frida.get_local_device().get_process(GAME_EXE).pid
        buf = ctypes.create_unicode_buffer(1024)
        t0 = time.time()
        check_at, misses = t0 + 30, 0
        while self.alive and time.time() - t0 < 300:
            if time.time() >= check_at:  # every 30 s the game process must still be there; two misses stop the answerer with a reason
                check_at = time.time() + 30
                try:
                    present = frida.get_local_device().get_process(GAME_EXE).pid == pid
                except Exception:
                    present = False
                misses = next_misses(misses, present)
                if misses >= 2:
                    self.log({'kind': 'startup-dialog', 'reason': f'gave up after {int(time.time() - t0)} s: {GAME_EXE} (pid {pid}) is gone (two checks 30 s apart)'})
                    return
            hits = []
            def cb(h, _):
                p = wintypes.DWORD()
                u.GetWindowThreadProcessId(h, ctypes.byref(p))
                if p.value == pid and u.GetClassNameW(h, buf, 64) and u.IsWindowVisible(h):
                    ids = [i for i in MOD_VERSION_DIALOG.values() if u.GetDlgItem(h, i)]
                    if is_mod_version_dialog(buf.value, ids):
                        hits.append(h)
                return True
            u.EnumWindows(ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)(cb), 0)
            for h in hits:
                u.GetWindowTextW(u.GetDlgItem(h, MOD_VERSION_DIALOG['text']), buf, 1024)
                btn = u.GetDlgItem(h, MOD_VERSION_DIALOG['continue'])
                # WM_COMMAND(BN_CLICKED) to the dialog, as the button itself would send it; PostMessage never blocks the probe
                ok = bool(u.PostMessageW(h, 0x0111, MOD_VERSION_DIALOG['continue'], btn))
                self.log({'kind': 'startup-dialog', 'reason': f"Continue {'posted' if ok else 'failed'}: {buf.value[:160]}"})
                if ok:
                    return
            time.sleep(1)

    def sample_memory(self):
        """Address-space timeline (memory.csv): shows what fills the 4 GB before an out-of-memory crash."""
        cols = ['time', 'commitMB', 'reserveOnlyMB', 'freeMB', 'largestFreeMB', 'imageMB', 'mappedMB', 'privateMB', 'threads', 'gcHeapMB', 'gcFreeMB']  # gc*: Boehm heap (docs/66 §6)
        with open(self.out / 'memory.csv', 'a', encoding='utf-8') as f:
            f.write(','.join(cols) + '\n')
            n, threads = 0, 0
            while self.alive:
                try:
                    m = self.script.exports_sync.address_space()
                    if n % 6 == 0:  # counting threads enumerates (suspends) them all; once a minute is enough
                        threads = self.script.exports_sync.status()['threads']
                    n += 1
                    m['threads'] = threads
                    m['time'] = time.strftime('%H:%M:%S')
                    f.write(','.join(str(m.get(c, '')) for c in cols) + '\n')
                    f.flush()
                    if m['largestFreeMB'] < 200:
                        self.log({'kind': 'low-address-space', 'largestFreeMB': m['largestFreeMB'], 'freeMB': m['freeMB']})
                except Exception:
                    pass  # game busy or gone; the detach handler reports that
                time.sleep(self.args.memory_interval)

    def game_window(self, iconic_ok=False):
        """Largest visible top-level game window as (area, hwnd, x, y, iconic); a minimized one (area 0) only with iconic_ok."""
        import ctypes
        from ctypes import wintypes
        u = ctypes.windll.user32
        if not hasattr(self, 'pid'):
            self.pid = frida.get_local_device().get_process(GAME_EXE).pid
        found = []
        def cb(h, _):
            p = wintypes.DWORD()
            u.GetWindowThreadProcessId(h, ctypes.byref(p))
            r = wintypes.RECT()
            if p.value == self.pid and u.IsWindowVisible(h) and u.GetWindowRect(h, ctypes.byref(r)):
                iconic = bool(u.IsIconic(h))
                if iconic_ok or not iconic:
                    found.append((0 if iconic else (r.right - r.left) * (r.bottom - r.top), h, r.left, r.top, iconic))
            return True
        u.EnumWindows(ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)(cb), 0)
        return max(found, default=None, key=lambda w: w[0])

    def window_state(self):
        """Game window state for clock_state (docs/85 §6): read-only."""
        import ctypes
        from ctypes import wintypes
        w = self.game_window(iconic_ok=True)
        if w is None:
            return {'found': False}
        u = ctypes.windll.user32
        fg, p = ctypes.WINFUNCTYPE(wintypes.HWND)(('GetForegroundWindow', u))(), wintypes.DWORD()
        u.GetWindowThreadProcessId(wintypes.HWND(fg), ctypes.byref(p))
        return {'found': True, 'iconic': w[4], 'foreground': p.value == self.pid}  # any game window in front

    def window_show(self, cmd):
        """window_minimize / window_restore for the clock-minimize-pause repro (docs/85 §6; Rodney approved exactly these two
        on 2026-10-17). SW_SHOWMINNOACTIVE (7) / SW_SHOWNOACTIVATE (4): never focus, never move. ShowWindowAsync posts the
        change, so a busy game thread cannot block the probe."""
        import ctypes
        from ctypes import wintypes
        w = self.game_window(iconic_ok=True)
        if w is None:
            return {'ok': False, 'error': 'no visible game window'}
        minimize = cmd == 'window_minimize'
        # only a real change: SW_SHOWNOACTIVATE on a window that is not minimized would un-maximize it
        acted = w[4] != minimize and bool(ctypes.windll.user32.ShowWindowAsync(wintypes.HWND(w[1]), 7 if minimize else 4))
        return {'ok': True, 'result': {'cmd': cmd, 'was_iconic': w[4], 'acted': acted}}

    def keep_window_place(self):
        """Records where Rodney puts the game window (probe-out/window.json) whenever he moves it, and moves a newly started
        game window there once, without focusing it (Rodney 2026-10-16 evening: runs go unattended; still never focus).
        Also logs every minimize and restore (a minimized window pauses the sim clock, docs/85 §3)."""
        import ctypes
        u = ctypes.windll.user32
        place = ROOT / 'probe-out' / 'window.json'
        saved = json.loads(place.read_text()) if place.exists() else None
        game_window = self.game_window

        first, t0, iconic = None, None, None
        while self.alive:
            any_w = game_window(iconic_ok=True)
            now_iconic = any_w[4] if any_w else None
            if now_iconic is not None and iconic is not None and now_iconic != iconic:
                state = 'minimized' if now_iconic else 'restored'
                self.log({'kind': 'window', 'reason': f'game window {state}', 'state': state,
                          'time': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())})
            if now_iconic is not None:
                iconic = now_iconic
            w = game_window()
            if w and w[0] > 200 * 200:
                _, h, x, y, _ = w
                if first is None:
                    first, t0 = (x, y), time.time()
                    print(f'[ts3probe] game window at {first}; saved place {saved}', flush=True)
                    if saved and first != (saved['x'], saved['y']):
                        # SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE: position only, no focus, no z-order change
                        if u.SetWindowPos(h, None, saved['x'], saved['y'], 0, 0, 0x0001 | 0x0004 | 0x0010):
                            first = (saved['x'], saved['y'])
                            print(f'[ts3probe] moved game window to saved place {saved}', flush=True)
                if (x, y) != first and (not saved or (x, y) != (saved['x'], saved['y'])):
                    saved = {'x': x, 'y': y}  # moved by Rodney: remember it
                    place.parent.mkdir(exist_ok=True)
                    place.write_text(json.dumps(saved))
                    print(f'[ts3probe] remembered game window place {saved}', flush=True)
            time.sleep(2)

    def watch_script_errors(self):
        """The game writes one ScriptError_*.xml per script exception, with the managed stack trace inside."""
        seen = set(glob.glob(str(USER_DIR / 'ScriptError_*.xml')))
        while self.alive:
            for path in sorted(set(glob.glob(str(USER_DIR / 'ScriptError_*.xml'))) - seen):
                seen.add(path)
                time.sleep(0.5)  # let the game finish writing
                text = Path(path).read_text(encoding='utf-8', errors='replace')
                err = {'kind': 'script-error', 'file': Path(path).name, 'time': time.strftime('%Y-%m-%dT%H:%M:%S'), 'xml': text}
                with self.lock:
                    self.script_errors.append(err)
                self.log(err)
            time.sleep(2)

    def handle(self, path, query):
        q = {k: v[0] for k, v in parse_qs(query).items()}
        rpc = self.script.exports_sync
        if path == 'status':
            sc = self.state_cmd
            return dict(rpc.status(), alive=self.alive, out=str(self.out), state_cmd=sc and sc[0],
                        state_cmd_ago_s=sc and round(time.time() - sc[1], 1))
        if path == 'threads':
            ts = rpc.threads(int(q.get('frames', 32)))
            for t in ts:
                t['stack'] = self.symbols.stack(t['stack'])
            return ts
        if path == 'net':  # network calls per API and target (start=1 hooks them once); does the game go online while playing?
            return rpc.net(q.get('start') == '1')
        if path == 'native':  # ts3native.asi counters (route cache, worker, budget), null when the plugin is not loaded; routeworker=0|1 / routebudget=0|1 / routeasync=0|1 flip them at runtime
            sw = [q.get(k) for k in ('routeworker', 'routebudget', 'routeasync', 'alloc', 'waterjps', 'vfresetmax')]  # vfresetmax=1: zero the [Verify] maxima after this read
            return rpc.native_counters(*[int(v) if v in ('0', '1') else None for v in sw])
        if path == 'nativeexport':  # one exported ts3native DWORD: name=ts3native_footprintMode [&value=N], N >= 0
            # any non-negative integer (2026-10-25: 0|1 only dropped HoldKeyed=2 and p6warm=2 writes of native_set silently)
            v = q.get('value')
            return rpc.native_export(q.get('name', ''), int(v) if v is not None and v.isdigit() else None, int(q.get('offset', '0'), 0))
        if path == 'routelock':  # routing-lock hold/wait and plan overlap (docs/18 step 1a); start=1 hooks once, stop=1 unhooks
            return rpc.route_lock(q.get('start') == '1', q.get('stop') == '1')
        if path == 'taskstall':  # scheduler-stage stalls >= ms with task + managed stack samples (docs/04 F17); start=1 hooks, stop=1 unhooks.
            # S3PP's SimulatorStageProfiler detours the same functions: turn it off first. Records go to probe-out/<session>-taskstall.jsonl.
            return rpc.task_stall(q.get('start') == '1', q.get('stop') == '1', int(q.get('ms', 0)), int(q.get('sample', 0)), int(q.get('n', 10)))
        if path == 'managedstack':  # C# call stack of the sim thread, innermost first (interpreter frames)
            return rpc.interp_stack(int(q.get('frames', 64)))
        if path == 'vcache':  # docs/133 §5: snap=1 one slice of the callvirt inline-cache snapshot (repeat until done); budget=s per
            # snapshot, slice=ms of work, wait=ms for the next sim tick, dry=N methods only, max=method cap; out=PATH writes the union (read-only)
            o = {k: int(q[k]) for k in ('slice', 'wait', 'dry', 'max') if k in q}
            return rpc.vcache(q.get('snap') == '1', int(q.get('budget', 120)), q.get('out'), o)
        if path == 'interp':  # Mono interpreter frame chain of the sim thread (read-only)
            return rpc.interp_frames(int(q.get('frames', 32)))
        if path in ('simstack', 'threadstack') and not self.sample_lock.acquire(blocking=False):
            return {'error': 'busy: previous stack sample still running', 'thread': None, 'stack': [], 'threads': []}
        if path == 'simstack':  # sim thread only; safe for frequent use (the all-thread /threads is not)
            try:
                r = rpc.sim_stack(int(q.get('frames', 24)))
                r['stack'] = self.symbols.stack([f.rstrip('?') for f in r['stack']])
                return r
            finally:
                self.sample_lock.release()
        if path == 'threadstack':  # which=main (default) | <thread id> | all (every non-sim game thread, tagged); never the sim thread
            try:
                r = rpc.thread_stacks(q.get('which', 'main'), int(q.get('frames', 24)))
                for t in r['threads']:
                    if t['stack']:
                        t['stack'] = self.symbols.stack([f.rstrip('?') for f in t['stack']])
                return r
            finally:
                self.sample_lock.release()
        if path == 'memory':
            return rpc.address_space()
        if path == 'allocations':  # allocations >= minMB grouped by AllocationBase, with type and image owner (docs/08 M9), read-only
            return rpc.allocations(float(q.get('minMB', 32)))
        if path == 'mallochook':  # first 5 bytes of MSVCR80!malloc/free and the module a jmp there leads to (S3SS mimalloc), read-only
            return rpc.malloc_hook()
        if path == 'rescache':  # resource cache budget and fill (docs/08 phase 0), read-only
            return rpc.res_cache()
        if path == 'modules':
            self.symbols.refresh_modules()
            return rpc.modules()
        if path == 'read':
            return {'addr': q['addr'], 'hex': rpc.read(q['addr'], int(q.get('len', 64)))}
        if path == 'string':
            return {'addr': q['addr'], 'text': rpc.read_string(q['addr'], q.get('kind', 'ascii'))}
        if path == 'symbolize':
            return {'name': self.symbols.name(q['addr'])}
        if path == 'events':
            evs = rpc.events(int(q.get('since', 0)))
            for e in evs:
                if 'stack' in e:
                    e['stack'] = self.symbols.stack(e['stack'])
            return evs
        if path == 'reload':  # swap tools/probe/agent.js in place after editing it, without detaching from the game
            old = self.script
            self.script = self.session.create_script((ROOT / 'tools' / 'probe' / 'agent.js').read_text(encoding='utf-8'))
            self.script.on('message', self.on_message)
            self.script.load()
            old.unload()
            self.symbols.script = self.script
            if self.args.exception_stacks:
                self.script.exports_sync.enable_exceptions()
            return {'reloaded': 'tools/probe/agent.js'}
        if path == 'detach':  # stop probing without killing the host mid-call (that can take the game down)
            self.alive = False
            threading.Thread(target=self.session.detach, daemon=True).start()
            return {'detaching': True}
        if path == 'exceptions':  # per-site counts (only with --exception-stacks)
            return rpc.exception_counts()
        if path == 'agentstatus':
            return rpc.agent_status()
        if path == 'agent' and q['cmd'] in ('window_minimize', 'window_restore'):  # probe-side: the game's Mono has no user32
            return self.window_show(q['cmd'])
        if path == 'agent':  # in-game ts3agent command, e.g. agent?cmd=list_sims&args={"household":"active"}
            args = json.loads(q['args']) if 'args' in q else None
            if STATE_CMD.match(q['cmd']):
                self.state_cmd = (q['cmd'], time.time())
            reply = None
            with self.agent_lock:  # one request in the mailbox at a time
                seq = rpc.agent_send(q['cmd'], args)
                deadline = time.time() + int(q.get('timeout', 30000)) / 1000
                while time.time() < deadline and reply is None:
                    reply = rpc.agent_reply(seq)
                    if reply is None:
                        time.sleep(0.02)
            if reply is None:
                return {'ok': False, 'error': f"no reply to '{q['cmd']}' (game paused, loading or blocked?)", 'seq': seq}
            if q['cmd'] == 'clock_state' and reply.get('ok') and isinstance(reply.get('result'), dict):
                try:  # the agent reads the clock; the window state comes from here (docs/85 §6)
                    reply['result']['window'] = self.window_state()
                except Exception as e:
                    reply['result']['window'] = {'error': f'{type(e).__name__}: {e}'}
            return reply
        if path == 'scripterrors':
            with self.lock:
                return self.script_errors[int(q.get('since', 0)):]
        raise KeyError(path)


def launcher_product_flags():
    """The launcher's own record of the last real run: HKCU\\Software\\Electronic Arts\\Sims 3\\Launcher\\Settings
    LastRunIProducts (installed) AND LastRunEProducts (entitled), as the 8-digit lower-case hex the ini uses; None when
    either key is missing (the real launcher never ran)."""
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Electronic Arts\Sims 3\Launcher\Settings') as k:
            i, e = winreg.QueryValueEx(k, 'LastRunIProducts')[0], winreg.QueryValueEx(k, 'LastRunEProducts')[0]
        return f'{(int(i) & int(e)) & 0xFFFFFFFF:08x}'
    except OSError:
        return None


def set_selected_flags(text, flags):
    """LoadOption.ini text with `[productoption] selectedflags = <hex>`; None when it already has that value.
    The real launcher writes this mask at Play and the game mounts only the packs in it (docs/234); the launcher swap
    (docs/230) skips that write, so an EA launch sets it here. shortcut: appends a new [productoption] section when
    the key is missing, upgrade if the game rejects a duplicate section header."""
    import re
    m = re.search(r'(?im)^(\s*selectedflags\s*=\s*)([0-9a-fA-F]*)', text)
    if m and m.group(2).lower() == flags.lower():
        return None
    if m:
        return text[:m.start(2)] + flags + text[m.end(2):]
    return text.rstrip('\r\n') + ('\r\n' if text.strip() else '') + f'[productoption]\r\nselectedflags = {flags}\r\n'


def ensure_selected_flags(path, flags=None):
    """EA launch (docs/230 swap): make LoadOption.ini select the packs the launcher recorded; TS3_SELECTED_FLAGS overrides."""
    flags = flags or os.environ.get('TS3_SELECTED_FLAGS') or launcher_product_flags()
    if not flags:
        print(f'[ts3probe] WARN: no LastRunIProducts/LastRunEProducts in HKCU Launcher\\Settings: {path.name} left alone; '
              'run the real Sims3Launcher.exe once (Play) so it records the installed packs', flush=True)
        return
    try:
        old = path.read_text(encoding='utf-8') if path.exists() else ''
        new = set_selected_flags(old, flags)
        if new is None:
            print(f'[ts3probe] {path.name} selectedflags already {flags}', flush=True)
            return
        bak = path.with_name(path.name + '.bak-before-ea-launch')
        if old and not bak.exists():
            bak.write_text(old, encoding='utf-8')
        path.write_text(new, encoding='utf-8')
        print(f'[ts3probe] {path.name} selectedflags -> {flags} (launcher LastRunIProducts & LastRunEProducts; the swap skips the '
              'launcher write, docs/234)', flush=True)
    except OSError as e:
        print(f'[ts3probe] WARN: cannot set selectedflags in {path}: {e}', flush=True)


AUDIO_OFF = {**{k: '0' for k in ('voicelevel', 'soundfxlevel', 'ambientlevel', 'musiclevel')},
             **{k: '1' for k in ('voicemute', 'soundfxmute', 'ambientmute', 'musicmute')}}


def set_ini_keys(text, want):
    """Options.ini text with `key = value` set for every key in want (existing lines edited in place, missing ones appended)."""
    import re
    for k, v in want.items():
        pat = re.compile(rf'(?im)^([ \t]*{re.escape(k)}[ \t]*=[ \t]*)([^\r\n]*)')
        text = pat.sub(lambda m: m.group(1) + v, text) if pat.search(text) else text.rstrip('\r\n') + f'\r\n{k} = {v}\r\n'
    return text


def ini_values(text, keys):
    import re
    return {k: (m.group(1).strip() if (m := re.search(rf'(?im)^[ \t]*{re.escape(k)}[ \t]*=([^\r\n]*)', text)) else None) for k in keys}


def ensure_audio_off(path):
    """Unattended runs are silent: audio levels 0, mutes 1 in Options.ini before every launch, read back from disk. The game itself
    rewrites Options.ini (a new `lastdevice` resets every option to its defaults, audio on: docs/236 round 4), so a one-time manual
    edit does not hold. A failed read-back stops the launch. Also logs resolution and lastdevice (the 2026-11-02 launcher hangs
    followed such a reset)."""
    if not path.exists():
        print(f'[ts3probe] WARN: {path} missing (first start writes it): audio not set for this launch', flush=True)
        return
    old = path.read_bytes().decode('latin-1')  # bytes: keep the game's CRLF
    new = set_ini_keys(old, AUDIO_OFF)
    if new != old:
        path.write_bytes(new.encode('latin-1'))
    got = ini_values(path.read_bytes().decode('latin-1'), list(AUDIO_OFF) + ['resolution', 'lastdevice'])
    bad = {k: got[k] for k in AUDIO_OFF if got[k] != AUDIO_OFF[k]}
    if bad:
        raise SystemExit(f'[ts3probe] Options.ini audio read-back failed: {bad}; launch refused')
    print(f"[ts3probe] Options.ini audio off ({'set' if new != old else 'already'}, read back ok); resolution {got['resolution']}, "
          f"lastdevice {got['lastdevice']}", flush=True)


WTS_STATES = ['Active', 'Connected', 'ConnectQuery', 'Shadow', 'Disconnected', 'Idle', 'Listen', 'Reset', 'Down', 'Init']
NO_DISPLAY = 'session disconnected from console (another user is active); no display for D3D'


def session_verdict(cur, console, state):
    """None when the game can get a display, else the refusal reason. Locked stays Active, so it passes. Another user's session on the
    console leaves ours Disconnected and D3D device creation blocks (docs/236 round 4). An Active non-console session is RDP with a display."""
    return NO_DISPLAY if state == 'Disconnected' or (cur != console and state != 'Active') else None


def console_holder(cur, console):
    return 'our session' if cur == console else 'another user' if console != 0xFFFFFFFF else 'nobody'


def check_session():
    """(log line, refusal reason or None) from the WTS API (no quser text, no user names). Not Windows: always fine."""
    if os.name != 'nt':
        return 'console session state: n/a (not Windows)', None
    import ctypes
    from ctypes import wintypes
    k, w = ctypes.windll.kernel32, ctypes.windll.wtsapi32
    cur = wintypes.DWORD()
    k.ProcessIdToSessionId(os.getpid(), ctypes.byref(cur))
    console = k.WTSGetActiveConsoleSessionId()
    buf, n = ctypes.c_void_p(), wintypes.DWORD()
    if not w.WTSQuerySessionInformationW(None, cur.value, 8, ctypes.byref(buf), ctypes.byref(n)):  # 8 = WTSConnectState
        return f'console session state: query failed (error {ctypes.GetLastError()})', None  # unknown: do not block a launch on it
    code = ctypes.cast(buf, ctypes.POINTER(ctypes.c_int))[0]
    w.WTSFreeMemory(buf)
    state = WTS_STATES[code] if 0 <= code < len(WTS_STATES) else str(code)
    on_console = console_holder(cur.value, console)
    return f'console session state: session {cur.value} {state}, console held by {on_console}', session_verdict(cur.value, console, state)


def log_ea_processes():
    """EA launch: which EA app / launcher / game processes run before this launch (docs/236 round 4: the launcher-stage hangs had no
    process record to compare with the good launches)."""
    names = ('EADesktop.exe', 'EALauncher.exe', 'EABackgroundService.exe', 'TSLHelper.exe', 'Sims3Launcher.exe', 'TS3.exe')
    try:
        out = subprocess.run(['tasklist', '/NH', '/FO', 'CSV'], capture_output=True, text=True, timeout=20).stdout
    except (OSError, subprocess.SubprocessError) as e:
        print(f'[ts3probe] EA processes before launch: tasklist failed ({e})', flush=True)
        return
    seen = [line.split('","')[0].strip('"') + ' ' + line.split('","')[1] for line in out.splitlines() if line.split('","')[0].strip('"') in names]
    print(f"[ts3probe] EA processes before launch: {', '.join(seen) or 'none'}", flush=True)


def serve(args):
    global frida
    import frida
    probe = Probe(args)
    probe.start()

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            u = urlparse(self.path)
            try:
                body, code = probe.handle(u.path.strip('/'), u.query), 200
            except KeyError as e:
                body, code = {'error': f'unknown endpoint or parameter {e}'}, 404
            except Exception as e:  # report agent errors (game gone, bad address) instead of dying
                body, code = {'error': f'{type(e).__name__}: {e}'}, 500
            data = json.dumps(body, indent=1).encode()
            self.send_response(code)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *a):
            pass

    server = ThreadingHTTPServer(('127.0.0.1', args.port), Handler)  # loopback only: the probe reads game memory
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        while probe.alive:
            time.sleep(1)
    except KeyboardInterrupt:
        pass
    server.shutdown()
    print('[ts3probe] stopped', flush=True)


def get(args):
    try:
        with urllib.request.urlopen(f'http://127.0.0.1:{args.port}/{args.path}', timeout=60) as r:
            print(r.read().decode())
    except urllib.error.HTTPError as e:  # the server puts the reason in the body
        print(e.read().decode())
        sys.exit(1)


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    s = sub.add_parser('serve')
    s.add_argument('--launch', action='store_true', help=f'start {GAME_EXE} first')
    s.add_argument('--env', action='append', default=[], metavar='NAME=VALUE', help='environment for the launched game, e.g. GC_INITIAL_HEAP_SIZE=536870912 (docs/13)')
    s.add_argument('--world', help='save to auto-load, e.g. Baseline-SV.sims3 (passed as -worldFile)')
    s.add_argument('--profile', action='store_true', help='also load the stutter profiler')
    s.add_argument('--exception-stacks', action='store_true',
                   help='also log stacks for native exceptions (JS handler; the game raises thousands per second, which can starve the probe)')
    s.add_argument('--port', type=int, default=47891)
    s.add_argument('--memory-interval', type=float, default=10, help='seconds between address-space samples')
    s.add_argument('extra', nargs='*', help=f'extra {GAME_EXE} arguments after --')
    g = sub.add_parser('get')
    g.add_argument('path')
    g.add_argument('--port', type=int, default=47891)
    sub.add_parser('session-check', help='launch preflight only: prints the console session line; exit 3 + reason when no display; no game')
    sub.add_parser('self-check', help='asserts the start-up dialog matcher; no game needed')
    args = ap.parse_args()
    if args.cmd == 'self-check':
        assert is_mod_version_dialog('#32770', [1001, 1004, 1003]) and is_mod_version_dialog('#32770', [1003, 1004, 1001, 2])
        assert not is_mod_version_dialog('#32770', [1001, 1004]) and not is_mod_version_dialog('Canvas', [1001, 1004, 1003])
        ini = '[productoption]\r\nplayedflags = ffffffff\r\n\r\nselectedflags = 00000000\r\n'
        assert set_selected_flags(ini, '001fefff') == ini.replace('00000000', '001fefff')
        assert set_selected_flags(ini.replace('00000000', '001FEFFF'), '001fefff') is None
        assert set_selected_flags('', '001fefff') == '[productoption]\r\nselectedflags = 001fefff\r\n'
        assert [next_misses(m, p) for m, p in ((0, True), (0, False), (1, False), (1, True))] == [0, 1, 2, 0]
        assert launcher_product_flags() is None or len(launcher_product_flags()) == 8
        ini = 'musiclevel = 255\r\nmusicmute = 0\r\nfocusmute = 1\r\n'
        out = set_ini_keys(ini, AUDIO_OFF)
        assert ini_values(out, list(AUDIO_OFF) + ['focusmute']) == {**AUDIO_OFF, 'focusmute': '1'} and out.count('musiclevel') == 1
        assert set_ini_keys(out, AUDIO_OFF) == out
        assert all(STATE_CMD.match(c) for c in ('travel_vacation', 'load_save', 'play_household', 'cas_open', 'main_menu', 'switch_household'))
        assert not any(STATE_CMD.match(c) for c in ('auto_dialog', 'autopilot', 'time', 'game_state', 'pending_dialogs', 'do_interaction'))
        assert [console_holder(*a) for a in ((2, 2), (2, 1), (2, 0xFFFFFFFF))] == ['our session', 'another user', 'nobody']
        assert [session_verdict(*a) for a in ((2, 1, 'Disconnected'), (1, 1, 'Active'), (1, 1, 'Active'), (2, 1, 'Active'), (2, 1, 'Idle'))] == [NO_DISPLAY, None, None, None, NO_DISPLAY]
        assert check_session()[0].startswith('console session state:')
        print('self-check ok')
        return
    if args.cmd == 'session-check':
        line, why = check_session()
        print(line)
        if why:
            print(why)
            sys.exit(3)
        return
    serve(args) if args.cmd == 'serve' else get(args)


if __name__ == '__main__':
    main()
