"""Find direct x64 call sites (E8 rel32 and E9 rel32 tail jumps) that target given addresses in a PE.

Scans executable sections of the file on disk. Prints call-site VAs.
Usage: python find_callers.py <exe> 0x14102FCE0 [0x...]
"""
import mmap
import struct
import sys


def sections(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    image_base = struct.unpack_from("<Q", data, pe + 24 + 24)[0]
    out = []
    off = pe + 24 + opt_size
    for _ in range(nsec):
        name = data[off:off + 8].rstrip(b"\0").decode(errors="replace")
        vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", data, off + 8)
        chars = struct.unpack_from("<I", data, off + 36)[0]
        out.append((name, va, vsize, rawptr, rawsize, chars))
        off += 40
    return image_base, out


def main():
    exe = sys.argv[1]
    targets = {int(a, 16) for a in sys.argv[2:]}
    with open(exe, "rb") as f:
        data = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        base, secs = sections(data)
        for name, va, vsize, rawptr, rawsize, chars in secs:
            if not chars & 0x20000000:          # IMAGE_SCN_MEM_EXECUTE
                continue
            if rawsize > 100 * 1024 * 1024:     # skip the huge protected section
                print(f"skipping {name} ({rawsize >> 20} MB)")
                continue
            buf = data[rawptr:rawptr + rawsize]
            sec_va = base + va
            for op in (b"\xE8", b"\xE9"):
                i = buf.find(op)
                while i != -1 and i + 5 <= len(buf):
                    rel = struct.unpack_from("<i", buf, i + 1)[0]
                    tgt = sec_va + i + 5 + rel
                    if tgt in targets:
                        kind = "call" if op == b"\xE8" else "jmp "
                        print(f"{kind} {sec_va + i:#x} -> {tgt:#x}  ({name})")
                    i = buf.find(op, i + 1)


if __name__ == "__main__":
    main()
