"""Check leak-stress-drip.py's cas and hold events and the HTTPError body with a fake agent: no game, no network.
The fake game is in CAS from cas_open until cas_close; the first cas_close fails, so the driver must retry it.
Usage: python -I tools/harness/leak-stress-drip-cas-check.py [tools/harness/leak-stress-drip.py]"""
import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

spec = importlib.util.spec_from_file_location("sdm", sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "leak-stress-drip.py"))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
d = tempfile.mkdtemp(prefix="cas_hold_check_")
start, out = os.path.join(d, "t.start"), os.path.join(d, "t.out")
open(start, "w").close()
os.utime(start, (time.time() - 3600,) * 2)
calls = []
game = {"state": "LiveModeState", "closes": 0, "measure": 0, "refused": 0}


def fake(url, timeout=None):
    q = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)
    cmd = q["cmd"][0]
    calls.append((cmd, json.loads(q.get("args", ["null"])[0])))
    if len(calls) > 3000:  # a driver that never leaves CAS would run forever: end it, the asserts then fail
        with open(out, "w") as f:
            f.write("t done\n")
    if cmd == "cas_category" and sum(1 for c, _ in calls if c == "cas_category") == 2:
        raise urllib.error.HTTPError(url, 500, "Internal Server Error", {}, io.BytesIO(b'{"ok":false,"error":"no clothing panel"}'))
    if cmd in ("cas_open", "cas_category", "cas_close"):
        if game["measure"] > 0:  # the agent times each action until it settles and refuses the next one meanwhile (al368)
            game["refused"] += 1
            return io.BytesIO(json.dumps({"ok": False, "error": "previous CAS/Buy action is still being timed; wait and retry"}).encode())
        game["measure"] = 2  # the next two cas_state reads say measuring
    if cmd == "cas_state" and game["measure"] > 0:
        game["measure"] -= 1
        return io.BytesIO(json.dumps({"ok": True, "result": {"cas_ready": game["state"].startswith("CAS"), "measuring": True}}).encode())
    if cmd == "cas_open":
        game["state"] = "CASFullModeState"
    if cmd == "cas_close":
        game["closes"] += 1
        if game["closes"] == 2:  # the first close does nothing (as in al367), the retry works
            game["state"] = "LiveModeState"
            with open(out, "w") as f:
                f.write("t done\n")
    res = {"game_state": {"sub_state": game["state"]}, "cas_state": {"cas_ready": game["state"].startswith("CAS")},
           "hold_sims": {"held": 120, "added": 3, "destroyed": 7}}.get(cmd, {})
    return io.BytesIO(json.dumps({"ok": True, "result": res}).encode())


urllib.request.urlopen = fake
m.game_up = lambda: True
class FakeTime:  # a clock that moves only when the driver sleeps: no real waits, and the 180 s limits still end
    def __init__(self):
        self.now = time.time()

    def time(self):
        return self.now

    def sleep(self, s):
        self.now += s

    def __getattr__(self, name):
        return getattr(time, name)


m.time = FakeTime()
sys.argv = ["leak-stress-drip.py", start, out, "--start", "0", "--only", "hold,cas"]
log = io.StringIO()
with contextlib.redirect_stdout(log):
    m.main()
names = [c for c, _ in calls]
txt = log.getvalue()
assert "hold_sims" in names and names.index("hold_sims") < names.index("cas_open"), names
assert [a["category"] for c, a in calls if c == "cas_category"] == ["hair", "everyday", "tops"], calls
assert names.count("cas_close") == 2 and game["state"] == "LiveModeState", names
assert game["refused"] == 0, (game, names)
assert "hold 1: {\"held\": 120" in txt, txt
line = [l for l in txt.splitlines() if " cas 1: " in l][0]
assert "open 2 s, hair 2 s, everyday failed HTTP Error 500: Internal Server Error" in line, line
assert line.endswith(", tops 2 s, close not live after 180 s, close 2 s"), line
print("cas/hold check ok:", len(calls), "fake commands;", line[9:])
