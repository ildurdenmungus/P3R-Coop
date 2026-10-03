"""Find UE4 native exec thunks by name in a shipping exe.

UE registers natives as FNameNativePtrPair { const char* Name; FNativeFuncPtr Ptr; } arrays in .rdata.
For each requested name: find the ANSI string, find a pointer to it, read the next qword = exec thunk.
Usage: python find_natives.py <exe> NAME [NAME ...]
"""
import struct
import sys
import mmap


def sections(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    image_base = struct.unpack_from("<Q", data, pe + 24 + 24)[0]
    out = []
    off = pe + 24 + opt_size
    for i in range(nsec):
        name = data[off:off + 8].rstrip(b"\0").decode(errors="replace")
        vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", data, off + 8)
        out.append((name, va, vsize, rawptr, rawsize))
        off += 40
    return image_base, out


def off_to_va(image_base, secs, off):
    for name, va, vsize, rawptr, rawsize in secs:
        if rawptr <= off < rawptr + rawsize:
            return image_base + va + (off - rawptr)
    return None


def va_to_off(image_base, secs, addr):
    rva = addr - image_base
    for name, va, vsize, rawptr, rawsize in secs:
        if va <= rva < va + rawsize:
            return rawptr + (rva - va)
    return None


def main():
    exe, names = sys.argv[1], sys.argv[2:]
    with open(exe, "rb") as f:
        data = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        image_base, secs = sections(data)
        print("sections:", ", ".join(f"{n}@{va:#x}" for n, va, *_ in secs))
        for name in names:
            needle = b"\0" + name.encode() + b"\0"
            pos = data.find(needle)
            hits = 0
            while pos != -1 and hits < 6:
                str_va = off_to_va(image_base, secs, pos + 1)
                if str_va:
                    ptr = struct.pack("<Q", str_va)
                    p = data.find(ptr)
                    while p != -1:
                        if p % 8 == 0:
                            nxt = struct.unpack_from("<Q", data, p + 8)[0]
                            print(f"{name}: str {str_va:#x}  table@{off_to_va(image_base, secs, p):#x}  exec={nxt:#x}")
                            hits += 1
                        p = data.find(ptr, p + 1)
                pos = data.find(needle, pos + 1)
            if hits == 0:
                print(f"{name}: not found")


if __name__ == "__main__":
    main()
