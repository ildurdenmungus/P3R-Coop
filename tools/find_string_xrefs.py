"""Find code that references an ANSI string through RIP-relative LEA (lea r64, [rip+disp32]).

Usage: python find_string_xrefs.py <exe> <string> [<string> ...]
Prints the string's VA and every `lea` site in executable sections (except the huge protected one).
"""
import mmap
import struct
import sys


def sections(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    base = struct.unpack_from("<Q", data, pe + 24 + 24)[0]
    out, off = [], pe + 24 + opt_size
    for _ in range(nsec):
        name = data[off:off + 8].rstrip(b"\0").decode(errors="replace")
        vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", data, off + 8)
        chars = struct.unpack_from("<I", data, off + 36)[0]
        out.append((name, va, rawptr, rawsize, chars))
        off += 40
    return base, out


def off_to_va(base, secs, o):
    for name, va, rawptr, rawsize, chars in secs:
        if rawptr <= o < rawptr + rawsize:
            return base + va + (o - rawptr)
    return None


def main():
    exe, strings = sys.argv[1], sys.argv[2:]
    f = open(exe, "rb")
    d = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    base, secs = sections(d)
    targets = {}
    for s in strings:
        o = d.find(b"\0" + s.encode() + b"\0")
        if o == -1:
            print(f"{s}: not found")
            continue
        va = off_to_va(base, secs, o + 1)
        targets[va] = s
        print(f"{s}: string at {va:#x}")
    for name, va, rawptr, rawsize, chars in secs:
        if not chars & 0x20000000 or rawsize > 100 * 1024 * 1024:
            continue
        buf = d[rawptr:rawptr + rawsize]
        sec_va = base + va
        i = 0
        n = len(buf) - 7
        while i < n:
            b0, b1, b2 = buf[i], buf[i + 1], buf[i + 2]
            if b0 in (0x48, 0x4C) and b1 == 0x8D and (b2 & 0xC7) == 0x05:
                disp = struct.unpack_from("<i", buf, i + 3)[0]
                tgt = sec_va + i + 7 + disp
                if tgt in targets:
                    print(f"  lea at {sec_va + i:#x} -> {targets[tgt]}")
            i += 1


if __name__ == "__main__":
    main()
