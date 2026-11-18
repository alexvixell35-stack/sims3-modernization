# Loads tools/harness/frame-times.js (presented-frame times, CModule hook on the main thread at 0xEC9F00, docs/162) into
# the running TS3W.exe in its own Frida session and keeps it until <stop file> exists, then unloads it (dispose flushes
# the CSV). For Linux/Wine runs, which have the frida module in the Wine Python but no frida_tools (profile-world.ts uses
# frida_tools.repl on Windows). Run with the Wine Python (-I):
#   wine python.exe -I tools/harness/frames-attach.py <out.csv> <stop file> [max seconds=86400]
# Paths are Windows paths (Z:\...). Output columns: frame-times.js header (epochMs,qpcMs,frameMs,workMs,fg,...).
import sys, time, pathlib, frida

out, stop = sys.argv[1], pathlib.Path(sys.argv[2])
limit = float(sys.argv[3]) if len(sys.argv) > 3 else 86400
src = (pathlib.Path(__file__).resolve().parent / 'frame-times.js').read_text(encoding='utf-8')
session = frida.attach('TS3W.exe')
script = session.create_script(src)
script.on('message', lambda m, d: print('[frames]', m.get('payload', m), flush=True))
script.load()
script.exports_sync.init('late', {'out': out.replace('\\', '/')})
print('[frames] attached ->', out, flush=True)
t0 = time.time()
try:
    while not stop.exists() and time.time() - t0 < limit:
        time.sleep(2)
finally:
    try:
        script.exports_sync.dispose()
        script.unload()
        session.detach()
    except Exception as e:  # game already gone: the CSV has what was drained
        print('[frames] unload:', e, flush=True)
    print('[frames] done after', round(time.time() - t0), 's', flush=True)
