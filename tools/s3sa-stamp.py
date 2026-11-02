"""Print or patch S3SA (0x073FAA07) header version and the version-2 UTF-16 build stamp of each resource in DBPF packages (docs/231 method). `--set-stamp NEW IN OUT` writes a personal-use copy with NEW (same length).
Header per s3pi ScriptResource: 1 byte version; if > 1: int32 char count + UTF-16 string. RefPack port of tools/extract-assemblies.ts."""
import struct, sys

S3SA = 0x073FAA07

def entries(data):
    assert data[:4] == b'DBPF', 'not DBPF'
    count, pos = struct.unpack_from('<I', data, 36)[0], struct.unpack_from('<I', data, 64)[0]
    flags = struct.unpack_from('<I', data, pos)[0]; pos += 4
    const = {}
    for i, name in enumerate(('type', 'group', 'ihi')):
        if flags & (1 << i):
            const[name] = struct.unpack_from('<I', data, pos)[0]; pos += 4
    for _ in range(count):
        e = dict(const)
        e['pos'] = pos
        for name in ('type', 'group', 'ihi'):
            if name not in const:
                e[name] = struct.unpack_from('<I', data, pos)[0]; pos += 4
        e['at'] = pos
        e['ilo'], e['off'], e['size'], e['dsize'], e['comp'] = struct.unpack_from('<IIIIH', data, pos); pos += 20
        e['sizeraw'] = e['size']
        e['size'] &= 0x7FFFFFFF
        yield e

def refpack(src):
    p = 0; flags = src[p]; p += 1
    assert src[p] == 0xFB, 'not RefPack'; p += 1
    nb = 4 if flags & 0x80 else 3
    if flags & 1: p += nb
    size = int.from_bytes(src[p:p + nb], 'big'); p += nb
    out = bytearray()
    while True:
        b0 = src[p]; p += 1; copy = off = 0
        if b0 < 0x80:
            b1 = src[p]; p += 1; plain = b0 & 3; copy = ((b0 & 0x1C) >> 2) + 3; off = ((b0 & 0x60) << 3) + b1 + 1
        elif b0 < 0xC0:
            b1, b2 = src[p], src[p + 1]; p += 2; plain = b1 >> 6; copy = (b0 & 0x3F) + 4; off = ((b1 & 0x3F) << 8) + b2 + 1
        elif b0 < 0xE0:
            b1, b2, b3 = src[p:p + 3]; p += 3; plain = b0 & 3; copy = ((b0 & 0x0C) << 6) + b3 + 5; off = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1
        elif b0 < 0xFC:
            plain = ((b0 & 0x1F) << 2) + 4
        else:
            plain = b0 & 3; out += src[p:p + plain]; break
        out += src[p:p + plain]; p += plain
        for _ in range(copy):
            out.append(out[-off])
    assert len(out) == size, f'RefPack size mismatch {len(out)} != {size}'
    return bytes(out)

def stamp(blob):
    ver = blob[0]
    if ver <= 1:
        return ver, None
    n = struct.unpack_from('<i', blob, 1)[0]
    return ver, blob[5:5 + 2 * n].decode('utf-16-le', 'replace')

def set_stamp(data, new):
    """Copy of DBPF `data` with every version-2 S3SA stamp replaced by `new` (same length). Patched blobs are stored
    uncompressed, appended after the old data; the index entry gets the new offset, sizes and comp flag 0. Other bytes untouched."""
    out = bytearray(data)
    for e in entries(data):
        if e['type'] != S3SA:
            continue
        blob = data[e['off']:e['off'] + e['size']]
        if e['comp'] == 0xFFFF:
            blob = refpack(blob)
        ver, old = stamp(blob)
        if ver <= 1:
            continue
        assert len(old) == len(new), 'stamp length differs'
        blob = blob[:5] + new.encode('utf-16-le') + blob[5 + 2 * len(old):]
        off = len(out); out += blob
        struct.pack_into('<IIIIH', out, e['at'], e['ilo'], off, (e['sizeraw'] & 0x80000000) | len(blob), len(blob), 0)
    return bytes(out)

def synth(blob, comp=0):
    """Tiny one-entry DBPF (count, index offset, flags 0, entry type/group/ihi/ilo/off/size/dsize/comp) for the self-check."""
    d = bytearray(96); d[:4] = b'DBPF'
    off = len(d); d += blob
    struct.pack_into('<I', d, 36, 1); struct.pack_into('<I', d, 64, len(d))
    d += struct.pack('<I', 0) + struct.pack('<IIIIIIIH', S3SA, 0, 1, 2, off, 0x80000000 | len(blob), len(blob), comp)
    return bytes(d)

if __name__ == '__main__':
    if len(sys.argv) == 5 and sys.argv[1] == '--set-stamp':  # --set-stamp NEW IN OUT
        data = open(sys.argv[3], 'rb').read()
        res = set_stamp(data, sys.argv[2])
        open(sys.argv[4], 'wb').write(res)
        print(f'patched {len(data)} -> {len(res)} B')
        sys.exit(0)
    if len(sys.argv) == 1:  # self-check
        s = '1.0.0.18'.encode('utf-16-le')
        assert stamp(b'' + struct.pack('<i', len(s) // 2) + s) == (2, '1.0.0.18')
        assert stamp(b'xx') == (1, None)
        # RefPack: 0x10 0xFB, 3-byte size 6, literal run (0xE0 = 4 plain), stop code with 2 plain
        assert refpack(bytes([0x10, 0xFB, 0, 0, 6, 0xE0]) + b'abcd' + bytes([0xFE]) + b'ef') == b'abcdef'
        old = b'' + struct.pack('<i', 8) + '0.2.0.32'.encode('utf-16-le') + b'tail'
        pk = set_stamp(synth(old), '1.0.0.18')
        e = next(entries(pk)); assert e['comp'] == 0 and stamp(pk[e['off']:e['off'] + e['size']]) == (2, '1.0.0.18')
        assert pk[e['off'] + e['size'] - 4:e['off'] + e['size']] == b'tail' and pk[:len(synth(old))].count(b'DBPF') == 1
        print('self-check ok'); sys.exit(0)
    for path in sys.argv[1:]:
        data = open(path, 'rb').read()
        for e in entries(data):
            if e['type'] != S3SA:
                continue
            blob = data[e['off']:e['off'] + e['size']]
            if e['comp'] == 0xFFFF:
                blob = refpack(blob)
            ver, s = stamp(blob)
            print(f"{path.rsplit('/', 1)[-1]}: S3SA {e['ihi']:08X}{e['ilo']:08X} header v{ver} stamp {s!r}")
