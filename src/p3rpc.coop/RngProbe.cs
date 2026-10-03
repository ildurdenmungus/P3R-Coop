using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace p3rpc.coop;

/// <summary>
/// Recon for floor sync: who calls the CRT rand()/srand(), how often, and on which thread.
/// P3R.exe's import-table slots for rand/srand are pointed at small hand-assembled stubs that record
/// (return address, thread id[, seed]) into ring buffers and then jump to the real function.
/// ucrtbase itself is never patched (an inline hook there relocated rand's `call` and crashed the game).
/// A background thread summarises the buffers into coop.log once per second.
/// </summary>
public unsafe class RngProbe
{
    private const string CrtUtility = "api-ms-win-crt-utility-l1-1-0.dll";
    private const int RandSlots = 65536;  // 16 bytes each: return address, thread id
    private const int SrandSlots = 256;   // 32 bytes each: return address, thread id, seed

    private readonly CoopLog _log;
    private readonly byte* _randBuf;
    private readonly int* _randIdx;
    private readonly byte* _srandBuf;
    private readonly int* _srandIdx;

    private (ulong Base, ulong End, string Name)[] _modules = Array.Empty<(ulong, ulong, string)>();

    public RngProbe(CoopLog log)
    {
        _log = log;
        var exe = (byte*)Process.GetCurrentProcess().MainModule!.BaseAddress;

        var randSlot = FindImportSlot(exe, CrtUtility, "rand");
        var srandSlot = FindImportSlot(exe, CrtUtility, "srand");
        if (randSlot == null || srandSlot == null)
        {
            log.Error($"IAT slots not found (rand={(nint)randSlot:X}, srand={(nint)srandSlot:X}); RNG probe off");
            return;
        }

        _randBuf = (byte*)NativeMemory.AllocZeroed(RandSlots * 16);
        _randIdx = (int*)NativeMemory.AllocZeroed(64);
        _srandBuf = (byte*)NativeMemory.AllocZeroed(SrandSlots * 32);
        _srandIdx = (int*)NativeMemory.AllocZeroed(64);

        RealSrand = *srandSlot;
        var randStub = Emit(RandStub(*randSlot));
        var srandStub = Emit(SrandStub(*srandSlot, forceSeed: 0));   // seeding is targeted (DungeonProbe), not global
        Patch(randSlot, randStub);
        Patch(srandSlot, srandStub);

        log.Info($"RNG probe on (IAT): rand slot P3R.exe+0x{(ulong)randSlot - (ulong)exe:X}, " +
                 $"srand slot P3R.exe+0x{(ulong)srandSlot - (ulong)exe:X}");
        new Thread(ReportLoop) { IsBackground = true, Name = "coop-rng-report" }.Start();
    }

    // push rax/rcx/rdx; idx = xadd(*idxp, 1) & mask; buf[idx] = {ret, tid}; pop; jmp real
    private byte[] RandStub(nint real)
    {
        var b = new List<byte> { 0x50, 0x51, 0x52 };
        b.AddRange(new byte[] { 0x48, 0xB9 }); b.AddRange(BitConverter.GetBytes((ulong)_randIdx));  // mov rcx, idx
        b.AddRange(new byte[] { 0xB8, 1, 0, 0, 0 });                                                  // mov eax, 1
        b.AddRange(new byte[] { 0xF0, 0x0F, 0xC1, 0x01 });                                            // lock xadd [rcx], eax
        b.Add(0x25); b.AddRange(BitConverter.GetBytes(RandSlots - 1));                                // and eax, mask
        b.AddRange(new byte[] { 0x48, 0xC1, 0xE0, 0x04 });                                            // shl rax, 4
        b.AddRange(new byte[] { 0x48, 0xBA }); b.AddRange(BitConverter.GetBytes((ulong)_randBuf));  // mov rdx, buf
        b.AddRange(new byte[] { 0x48, 0x01, 0xC2 });                                                  // add rdx, rax
        b.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x18 });                                      // mov rcx, [rsp+24]
        b.AddRange(new byte[] { 0x48, 0x89, 0x0A });                                                  // mov [rdx], rcx
        b.AddRange(new byte[] { 0x31, 0xC9 });                                                        // xor ecx, ecx
        b.AddRange(new byte[] { 0x65, 0x48, 0x8B, 0x49, 0x48 });                                      // mov rcx, gs:[rcx+48h]
        b.AddRange(new byte[] { 0x48, 0x89, 0x4A, 0x08 });                                            // mov [rdx+8], rcx
        b.AddRange(new byte[] { 0x5A, 0x59, 0x58 });                                                  // pop rdx/rcx/rax
        AddJmp(b, real);
        return b.ToArray();
    }

    // like RandStub, plus the seed (rcx); optionally replaces the seed before jumping to the real srand
    private byte[] SrandStub(nint real, uint forceSeed)
    {
        var b = new List<byte> { 0x50, 0x52, 0x41, 0x50 };                                            // push rax/rdx/r8
        b.AddRange(new byte[] { 0x49, 0xB8 }); b.AddRange(BitConverter.GetBytes((ulong)_srandIdx)); // mov r8, idx
        b.AddRange(new byte[] { 0xB8, 1, 0, 0, 0 });                                                  // mov eax, 1
        b.AddRange(new byte[] { 0xF0, 0x41, 0x0F, 0xC1, 0x00 });                                      // lock xadd [r8], eax
        b.Add(0x25); b.AddRange(BitConverter.GetBytes(SrandSlots - 1));                               // and eax, mask
        b.AddRange(new byte[] { 0x48, 0xC1, 0xE0, 0x05 });                                            // shl rax, 5
        b.AddRange(new byte[] { 0x48, 0xBA }); b.AddRange(BitConverter.GetBytes((ulong)_srandBuf)); // mov rdx, buf
        b.AddRange(new byte[] { 0x48, 0x01, 0xC2 });                                                  // add rdx, rax
        b.AddRange(new byte[] { 0x4C, 0x8B, 0x44, 0x24, 0x18 });                                      // mov r8, [rsp+24]
        b.AddRange(new byte[] { 0x4C, 0x89, 0x02 });                                                  // mov [rdx], r8
        b.AddRange(new byte[] { 0x45, 0x31, 0xC0 });                                                  // xor r8d, r8d
        b.AddRange(new byte[] { 0x65, 0x4D, 0x8B, 0x40, 0x48 });                                      // mov r8, gs:[r8+48h]
        b.AddRange(new byte[] { 0x4C, 0x89, 0x42, 0x08 });                                            // mov [rdx+8], r8
        b.AddRange(new byte[] { 0x48, 0x89, 0x4A, 0x10 });                                            // mov [rdx+16], rcx
        b.AddRange(new byte[] { 0x41, 0x58, 0x5A, 0x58 });                                            // pop r8/rdx/rax
        if (forceSeed != 0) { b.Add(0xB9); b.AddRange(BitConverter.GetBytes(forceSeed)); }           // mov ecx, seed
        AddJmp(b, real);
        return b.ToArray();
    }

    private static void AddJmp(List<byte> b, nint target)
    {
        b.AddRange(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 });                                            // jmp [rip+0]
        b.AddRange(BitConverter.GetBytes((ulong)target));
    }

    private static nint Emit(byte[] code)
    {
        var mem = VirtualAlloc(0, (nuint)code.Length, 0x3000 /*COMMIT|RESERVE*/, 0x40 /*RWX*/);
        Marshal.Copy(code, 0, mem, code.Length);
        return mem;
    }

    private static void Patch(nint* slot, nint value)
    {
        VirtualProtect((nint)slot, 8, 0x04 /*RW*/, out var old);
        *slot = value;
        VirtualProtect((nint)slot, 8, old, out _);
    }

    /// <summary>Walks the in-memory import directory of a module and returns the IAT slot for dll!func.</summary>
    private static nint* FindImportSlot(byte* module, string dll, string func)
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

    public bool Active => _randIdx != null;

    /// <summary>Real CRT srand (not the stub), for seeding the calling thread's rand() state.</summary>
    public nint RealSrand { get; private set; }

    /// <summary>
    /// Runs <paramref name="call"/> and reports the rand() calls the current thread made during it
    /// (count + call sites). Other threads' calls in the same window are filtered out by thread id.
    /// </summary>
    public string Measure(Action call)
    {
        if (!Active) { call(); return "probe off"; }
        var tid = (ulong)GetCurrentThreadId();
        int before = Volatile.Read(ref *_randIdx);
        call();
        int after = Volatile.Read(ref *_randIdx);
        var hist = new Dictionary<ulong, int>();
        int mine = 0;
        for (int i = Math.Max(before, after - RandSlots); i < after; i++)
        {
            var e = _randBuf + (i & (RandSlots - 1)) * 16;
            if (*(ulong*)(e + 8) != tid) continue;
            mine++;
            var ret = *(ulong*)e;
            hist[ret] = hist.TryGetValue(ret, out var c) ? c + 1 : 1;
        }
        var lost = after - before > RandSlots ? " (buffer wrapped)" : "";
        var top = hist.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{Resolve(kv.Key)} x{kv.Value}");
        return $"{mine} rand on this thread{lost}: {string.Join(" | ", top)}";
    }

    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();

    private void ReportLoop()
    {
        int lastRand = 0, lastSrand = 0;
        var refresh = Stopwatch.StartNew();
        RefreshModules();
        while (true)
        {
            Thread.Sleep(1000);
            if (refresh.Elapsed.TotalSeconds > 10) { RefreshModules(); refresh.Restart(); }

            int s = Volatile.Read(ref *_srandIdx);
            for (int i = Math.Max(lastSrand, s - SrandSlots); i < s; i++)
            {
                var e = _srandBuf + (i & (SrandSlots - 1)) * 32;
                _log.Info($"srand(seed={*(uint*)(e + 16)}) tid={*(ulong*)(e + 8)} from {Resolve(*(ulong*)e)}");
            }
            lastSrand = s;

            int r = Volatile.Read(ref *_randIdx);
            int n = r - lastRand;
            if (n > 0)
            {
                var hist = new Dictionary<(ulong, ulong), int>();
                for (int i = Math.Max(lastRand, r - RandSlots); i < r; i++)
                {
                    var e = _randBuf + (i & (RandSlots - 1)) * 16;
                    var key = (*(ulong*)e, *(ulong*)(e + 8));
                    hist[key] = hist.TryGetValue(key, out var c) ? c + 1 : 1;
                }
                var top = hist.OrderByDescending(kv => kv.Value).Take(6)
                    .Select(kv => $"{Resolve(kv.Key.Item1)} tid={kv.Key.Item2} x{kv.Value}");
                _log.Quiet($"rand {n}/s: {string.Join(" | ", top)}");
            }
            lastRand = r;
        }
    }

    private void RefreshModules()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            _modules = p.Modules.Cast<ProcessModule>()
                .Select(m => ((ulong)m.BaseAddress, (ulong)m.BaseAddress + (ulong)m.ModuleMemorySize, m.ModuleName))
                .ToArray();
        }
        catch { /* keep the previous list */ }
    }

    private string Resolve(ulong addr)
    {
        foreach (var (b, e, name) in _modules)
            if (addr >= b && addr < e) return $"{name}+0x{addr - b:X}";
        return $"0x{addr:X}";
    }

    [DllImport("kernel32")] private static extern nint VirtualAlloc(nint addr, nuint size, uint type, uint protect);
    [DllImport("kernel32")] private static extern bool VirtualProtect(nint addr, nuint size, uint protect, out uint old);
}
