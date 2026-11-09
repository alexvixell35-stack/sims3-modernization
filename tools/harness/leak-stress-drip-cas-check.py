"""Check stress_drip_m.py's cas and hold events and the HTTPError body with a fake agent: no game, no network.
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
game = {"state": "LiveModeState", "closes": 0}


def fake(url, timeout=None):
    q = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)
    cmd = q["cmd"][0]
    calls.append((cmd, json.loads(q.get("args", ["null"])[0])))
    if cmd == "cas_category" and sum(1 for c, _ in calls if c == "cas_category") == 2:
        raise urllib.error.HTTPError(url, 500, "Internal Server Error", {}, io.BytesIO(b'{"ok":false,"error":"no clothing panel"}'))
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
assert "hold 1: {\"held\": 120" in txt, txt
line = [l for l in txt.splitlines() if " cas 1: " in l][0]
assert "open 0 s, hair 0 s, everyday failed HTTP Error 500: Internal Server Error" in line, line
assert line.endswith(", tops 0 s, close not live after 180 s, close 0 s"), line
print("cas/hold check ok:", len(calls), "fake commands;", line[9:])
