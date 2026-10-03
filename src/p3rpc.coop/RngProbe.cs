using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p3rpc.coop;

/// <summary>
/// Recon for floor sync: who calls the CRT rand()/srand(), how often, and on which thread.
/// Callers are captured by tiny asm hooks into ring buffers (no managed transition per rand call);
/// a background thread summarises them into coop.log once per second.
/// </summary>
public unsafe class RngProbe
{
    private const int RandSlots = 8192;   // 16 bytes each: return address, thread id
    private const int SrandSlots = 256;   // 32 bytes each: return address, thread id, seed

    private readonly CoopLog _log;
    private readonly uint _forceSeed;
    private readonly IAsmHook? _randAsm;
    private readonly IAsmHook? _srandAsm;
    private readonly IHook<SrandFn>? _srandHook;

    private readonly byte* _randBuf;
    private readonly int* _randIdx;
    private readonly byte* _srandBuf;
    private readonly int* _srandIdx;

    private (ulong Base, ulong End, string Name)[] _modules = Array.Empty<(ulong, ulong, string)>();

    [Function(CallingConventions.Microsoft)]
    public delegate void SrandFn(uint seed);

    public RngProbe(IReloadedHooks hooks, CoopLog log, uint forceSeed)
    {
        _log = log;
        _forceSeed = forceSeed;

        var ucrt = GetModuleHandle("ucrtbase.dll");
        var rand = ucrt == 0 ? 0 : GetProcAddress(ucrt, "rand");
        var srand = ucrt == 0 ? 0 : GetProcAddress(ucrt, "srand");
        if (rand == 0 || srand == 0)
        {
            log.Error($"ucrtbase rand/srand not found (ucrt=0x{ucrt:X})");
            return;
        }

        _randBuf = (byte*)NativeMemory.AllocZeroed(RandSlots * 16);
        _randIdx = (int*)NativeMemory.AllocZeroed(64);
        _srandBuf = (byte*)NativeMemory.AllocZeroed(SrandSlots * 32);
        _srandIdx = (int*)NativeMemory.AllocZeroed(64);

        _randAsm = hooks.CreateAsmHook(new[]
        {
            "use64",
            "push rax", "push rcx", "push rdx",
            $"mov rcx, 0x{(ulong)_randIdx:X}",
            "mov eax, 1",
            "lock xadd dword [rcx], eax",
            $"and eax, 0x{RandSlots - 1:X}",
            "shl rax, 4",
            $"mov rdx, 0x{(ulong)_randBuf:X}",
            "add rdx, rax",
            "mov rcx, [rsp+24]",            // return address (3 pushes above it)
            "mov [rdx], rcx",
            "mov rcx, [gs:0x48]",           // TEB.ClientId.UniqueThread
            "mov [rdx+8], rcx",
            "pop rdx", "pop rcx", "pop rax",
        }, (long)rand, AsmHookBehaviour.ExecuteFirst).Activate();

        _srandAsm = hooks.CreateAsmHook(new[]
        {
            "use64",
            "push rax", "push rdx", "push r8",
            $"mov r8, 0x{(ulong)_srandIdx:X}",
            "mov eax, 1",
            "lock xadd dword [r8], eax",
            $"and eax, 0x{SrandSlots - 1:X}",
            "shl rax, 5",
            $"mov rdx, 0x{(ulong)_srandBuf:X}",
            "add rdx, rax",
            "mov r8, [rsp+24]",
            "mov [rdx], r8",
            "mov r8, [gs:0x48]",
            "mov [rdx+8], r8",
            "mov [rdx+16], rcx",            // seed (first arg)
            "pop r8", "pop rdx", "pop rax",
        }, (long)srand, AsmHookBehaviour.ExecuteFirst).Activate();

        if (forceSeed != 0)
            _srandHook = hooks.CreateHook<SrandFn>(SrandImpl, (long)srand).Activate();

        log.Info($"RNG probe on: rand=0x{rand:X} srand=0x{srand:X}");
        new Thread(ReportLoop) { IsBackground = true, Name = "coop-rng-report" }.Start();
    }

    private void SrandImpl(uint seed) => _srandHook!.OriginalFunction(_forceSeed);

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

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);
}
