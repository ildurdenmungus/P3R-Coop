using System.Runtime.InteropServices;

namespace p3rpc.coop;

/// <summary>Import-table (IAT) helpers: find a module's slot for dll!func, write executable stubs, repoint slots.</summary>
public static unsafe class ImportSlots
{
    /// <summary>Walks the in-memory import directory of a module and returns the IAT slot for dll!func.</summary>
    public static nint* Find(byte* module, string dll, string func)
    {
        var nt = module + *(int*)(module + 0x3C);
        var importRva = *(uint*)(nt + 0x18 + 0x78);          // OptionalHeader64.DataDirectory[1].VirtualAddress
        if (importRva == 0) return null;
        for (var desc = module + importRva; *(uint*)(desc + 12) != 0; desc += 20)
        {
            var name = Marshal.PtrToStringAnsi((nint)(module + *(uint*)(desc + 12)));
            if (!string.Equals(name, dll, StringComparison.OrdinalIgnoreCase)) continue;
            var lookup = *(uint*)desc != 0 ? *(uint*)desc : *(uint*)(desc + 16);
            var thunks = (ulong*)(module + lookup);
            var slots = (nint*)(module + *(uint*)(desc + 16));
            for (int i = 0; thunks[i] != 0; i++)
            {
                if ((thunks[i] & 0x8000000000000000UL) != 0) continue;   // by ordinal
                var fn = Marshal.PtrToStringAnsi((nint)(module + (uint)thunks[i] + 2));
                if (fn == func) return &slots[i];
            }
        }
        return null;
    }

    /// <summary>Copies machine code into fresh executable memory.</summary>
    public static nint Emit(byte[] code)
    {
        var mem = VirtualAlloc(0, (nuint)code.Length, 0x3000 /*COMMIT|RESERVE*/, 0x40 /*RWX*/);
        Marshal.Copy(code, 0, mem, code.Length);
        return mem;
    }

    /// <summary>Points an IAT slot somewhere else (slots live in read-only pages after load).</summary>
    public static void Patch(nint* slot, nint value)
    {
        VirtualProtect((nint)slot, 8, 0x04 /*RW*/, out var old);
        *slot = value;
        VirtualProtect((nint)slot, 8, old, out _);
    }

    [DllImport("kernel32")] private static extern nint VirtualAlloc(nint addr, nuint size, uint type, uint protect);
    [DllImport("kernel32")] private static extern bool VirtualProtect(nint addr, nuint size, uint protect, out uint old);
}
