#!/usr/bin/env python3
"""Dump the script tuning XML (resource type 0x0333406C) out of Sims 3 .package
files, read-only, so a static finding can be checked against the shipped
[Tunable] values instead of the defaults in the decompiled code (docs/587).

Usage: python3 -I tools/static/tuning-xml-dump.py <out-dir> <package> [<package> ...]
Writes <out-dir>/<instance-hex>.xml per resource (later packages overwrite earlier
ones, so pass base game first and packs after, like the game's load order).
Then: grep -l kSomeTunable <out-dir>/*.xml
Never commit the output: it is EA data.
"""
import os, struct, sys

XML_TYPE = 0x0333406C


def refpack(data):
    """Decompress EA RefPack (QFS)."""
    p = 0
    flags = data[0]
    p = 2
    if flags & 0x80:
        size = int.from_bytes(data[p:p + 4], "big"); p += 4
    else:
        size = int.from_bytes(data[p:p + 3], "big"); p += 3
    out = bytearray()
    n = len(data)
    while p < n:
        b0 = data[p]
        if b0 < 0x80:
            b1 = data[p + 1]; p += 2
            plain = b0 & 3
            clen = ((b0 & 0x1C) >> 2) + 3
            off = ((b0 & 0x60) << 3) + b1 + 1
        elif b0 < 0xC0:
            b1, b2 = data[p + 1], data[p + 2]; p += 3
            plain = (b1 & 0xC0) >> 6
            clen = (b0 & 0x3F) + 4
            off = ((b1 & 0x3F) << 8) + b2 + 1
        elif b0 < 0xE0:
            b1, b2, b3 = data[p + 1], data[p + 2], data[p + 3]; p += 4
            plain = b0 & 3
            clen = ((b0 & 0x0C) << 6) + b3 + 5
            off = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1
        elif b0 < 0xFC:
            p += 1
            plain = ((b0 & 0x1F) << 2) + 4
            clen = 0; off = 0
        else:
            p += 1
            plain = b0 & 3
            clen = 0; off = 0
        out += data[p:p + plain]; p += plain
        for _ in range(clen):
            out.append(out[-off])
        if b0 >= 0xFC:
            break
    return bytes(out[:size])


def resources(path):
    with open(path, "rb") as f:
        buf = f.read()
    if buf[:4] != b"DBPF":
        return
    count = struct.unpack_from("<I", buf, 36)[0]
    ipos = struct.unpack_from("<I", buf, 64)[0]
    flags = struct.unpack_from("<I", buf, ipos)[0]
    p = ipos + 4
    const = {}
    for i, name in enumerate(("type", "group", "insthi")):
        if flags & (1 << i):
            const[name] = struct.unpack_from("<I", buf, p)[0]; p += 4
    for _ in range(count):
        e = {}
        for name in ("type", "group", "insthi"):
            if name in const:
                e[name] = const[name]
            else:
                e[name] = struct.unpack_from("<I", buf, p)[0]; p += 4
        instlo, off, fsize, msize, comp = struct.unpack_from("<IIIIH", buf, p); p += 20  # + 2 unknown bytes
        fsize &= 0x7FFFFFFF
        if e["type"] != XML_TYPE:
            continue
        raw = buf[off:off + fsize]
        if comp == 0xFFFF:
            try:
                raw = refpack(raw)
            except Exception:
                continue
        yield (e["insthi"] << 32) | instlo, raw


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    n = 0
    for pkg in sys.argv[2:]:
        for inst, data in resources(pkg):
            with open(os.path.join(out, f"{inst:016x}.xml"), "wb") as f:
                f.write(data)
            n += 1
    print(f"{n} XML resources written to {out}")


if __name__ == "__main__":
    main()
