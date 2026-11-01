"""Dump the main module image of a running TS3.exe (EA app 1.69) or TS3W.exe (Steam 1.67) from memory.

The EA app TS3.exe keeps .text encrypted on disk (.ooa section); only the image in memory after start is readable.
Read-only: Frida attach plus Memory.readByteArray, no writes into the game process. Does not focus the game window.

  1. Start the game from the EA app and reach the main menu (code is decrypted by then).
  2. python tools/probe/dump-image.py --out probe-out/ts3-ea169.bin [--name TS3.exe] [--wait 60]
     writes <out> (memory layout, rva = file offset) and <out>.json {name, base, size, timestamp, sections}.
  3. node tools/sigmap.ts match --image probe-out/ts3-ea169.bin --name ea169     (see header of tools/sigmap.ts)
  python tools/probe/dump-image.py --self-check      parsing helpers only, no game, no frida
Waits (poll 1 s, up to --wait seconds) until the first 256 KB of .text has entropy below 7.2 bits/byte (encrypted is ~8).
Needs pip frida (same as ts3probe.py). Never commit the dump (EA game code): probe-out/ is ignored.
"""
import argparse, json, math, struct, sys, time
from pathlib import Path

NAMES = ('TS3.exe', 'TS3W.exe')


def parse_header(h):
    """-> dict(timestamp, size, sections[{name, va, vsize, exec}]) from PE header bytes."""
    if h[:2] != b'MZ':
        raise ValueError('no MZ')
    pe = struct.unpack_from('<I', h, 0x3c)[0]
    if h[pe:pe + 4] != b'PE\0\0':
        raise ValueError('no PE signature')
    nsec, ts, optsz = struct.unpack_from('<HIxxxxxxxxH', h, pe + 6)[0], struct.unpack_from('<I', h, pe + 8)[0], struct.unpack_from('<H', h, pe + 20)[0]
    opt = pe + 24
    size = struct.unpack_from('<I', h, opt + 56)[0]
    secs = []
    for i in range(nsec):
        o = opt + optsz + i * 40
        name = h[o:o + 8].split(b'\0')[0].decode('latin1')
        vsize, va = struct.unpack_from('<II', h, o + 8)
        chars = struct.unpack_from('<I', h, o + 36)[0]
        secs.append({'name': name, 'va': va, 'vsize': vsize, 'exec': bool(chars & 0x20000000)})
    return {'timestamp': ts, 'size': size, 'sections': secs}


def entropy(b):
    if not b:
        return 0.0
    c = [0] * 256
    for x in b:
        c[x] += 1
    n = len(b)
    return -sum(v / n * math.log2(v / n) for v in c if v)


def self_check():
    h = bytearray(0x400)
    h[0:2] = b'MZ'; struct.pack_into('<I', h, 0x3c, 0x80)
    h[0x80:0x84] = b'PE\0\0'; struct.pack_into('<H', h, 0x86, 1); struct.pack_into('<I', h, 0x88, 0x6707155C); struct.pack_into('<H', h, 0x94, 224)
    struct.pack_into('<I', h, 0x98 + 56, 0x20000)
    sh = 0x98 + 224
    h[sh:sh + 5] = b'.text'; struct.pack_into('<II', h, sh + 8, 0x1000, 0x1000); struct.pack_into('<I', h, sh + 36, 0x60000020)
    r = parse_header(bytes(h))
    assert r['timestamp'] == 0x6707155C and r['size'] == 0x20000 and r['sections'] == [{'name': '.text', 'va': 0x1000, 'vsize': 0x1000, 'exec': True}], r
    assert entropy(bytes(range(256)) * 16) > 7.99 and entropy(b'\x55\x8b\xec' * 1000) < 2
    try:
        parse_header(b'xx' * 64); raise SystemExit('bad header accepted')
    except ValueError:
        pass
    print('dump-image self-check ok')


AGENT = """
rpc.exports = {
  info: function (name) { var m = Process.findModuleByName(name); return m ? { base: m.base.toString(), size: m.size } : null; },
  read: function (addr, len) { try { return ptr(addr).readByteArray(len); } catch (e) { return null; } }
};
"""


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0], formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    ap.add_argument('--out', help='output file (default probe-out/<name>-<timestamp>.bin)')
    ap.add_argument('--name', help='process/module name (default: TS3.exe, else TS3W.exe)')
    ap.add_argument('--wait', type=int, default=60, help='seconds to wait for decrypted .text (default 60)')
    ap.add_argument('--self-check', action='store_true')
    a = ap.parse_args()
    if a.self_check:
        return self_check()
    import frida
    sess, name = None, None
    for n in ([a.name] if a.name else NAMES):
        try:
            sess, name = frida.attach(n), n
            break
        except frida.ProcessNotFoundError:
            pass
    if not sess:
        raise SystemExit('no running ' + ' or '.join([a.name] if a.name else NAMES))
    script = sess.create_script(AGENT)
    script.load()
    ex = script.exports_sync
    mod = ex.info(name)
    if not mod:
        raise SystemExit('module %s not found' % name)
    base, size = int(mod['base'], 16), mod['size']
    hdr = ex.read(hex(base), 0x1000)
    meta = parse_header(bytes(hdr))
    text = next((s for s in meta['sections'] if s['exec']), None)
    if not text:
        raise SystemExit('no executable section')
    t0 = time.time()
    while True:
        sample = ex.read(hex(base + text['va']), min(text['vsize'], 0x40000))
        e = entropy(bytes(sample)) if sample else 8.0
        print('[dump-image] %s .text entropy %.2f' % (name, e), flush=True)
        if e < 7.2:
            break
        if time.time() - t0 > a.wait:
            raise SystemExit('.text still looks encrypted after %d s (entropy %.2f); reach the main menu and retry' % (a.wait, e))
        time.sleep(1)
    out = Path(a.out) if a.out else Path('probe-out') / ('%s-%d.bin' % (name, int(time.time())))
    out.parent.mkdir(parents=True, exist_ok=True)
    unread = 0
    with open(out, 'wb') as f:
        for off in range(0, size, 0x10000):
            n = min(0x10000, size - off)
            chunk = ex.read(hex(base + off), n)
            if chunk is None:  # unmapped pages inside the chunk: read per page, zero the gaps
                chunk = b''
                for p in range(0, n, 0x1000):
                    pg = ex.read(hex(base + off + p), min(0x1000, n - p))
                    unread += pg is None
                    chunk += bytes(pg) if pg is not None else b'\0' * min(0x1000, n - p)
            f.write(bytes(chunk))
    out.with_name(out.name + '.json').write_text(json.dumps({'name': name, 'base': hex(base), 'size': size, 'timestamp': hex(meta['timestamp']), 'sections': meta['sections'], 'unreadablePages': unread}, indent=1))
    sess.detach()
    print('[dump-image] wrote %s (%d bytes, base %s, timestamp %s, %d unreadable pages)' % (out, size, hex(base), hex(meta['timestamp']), unread))


if __name__ == '__main__':
    main()
