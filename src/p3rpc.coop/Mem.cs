using System.Runtime.InteropServices;

namespace p3rpc.coop;

/// <summary>
/// Guard for reading game memory through pointers that may not be set up yet. A fault inside the UWorld::Tick hook
/// kills the game (coreclr fail-fast, 2026-10-07: reading the field manager's state at the title screen).
/// </summary>
public static unsafe class Mem
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public nint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32")] private static extern nint VirtualQuery(nint address, out MemoryBasicInformation info, nint length);

    private const uint MemCommit = 0x1000, PageNoAccess = 0x01, PageGuard = 0x100;

    /// <summary>True when [address, address+size) lies in committed, readable memory.</summary>
    public static bool Readable(nint address, int size)
    {
        if (address < 0x10000) return false;
        if (VirtualQuery(address, out var mbi, sizeof(MemoryBasicInformation)) == 0) return false;
        if (mbi.State != MemCommit || (mbi.Protect & (PageNoAccess | PageGuard)) != 0 || mbi.Protect == 0) return false;
        return address + size <= mbi.BaseAddress + mbi.RegionSize;
    }

    /// <summary>The field manager's state (+0x280; 2/3 = walking in the field), or -1 if it can't be read.</summary>
    public static int FieldState(nint fieldManager) =>
        Readable(fieldManager + 0x280, 4) ? *(int*)(fieldManager + 0x280) : -1;
}
