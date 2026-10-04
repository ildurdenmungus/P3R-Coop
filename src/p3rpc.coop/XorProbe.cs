using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p3rpc.coop;

/// <summary>
/// Research: who uses the game's xorshift128 "random in [0,n)" helper (P3R.exe+0x11AC2B0, state* in rcx,
/// n in edx; state = 4 dwords). An entry asm hook records (return address, state pointer) into a ring buffer
/// (no managed transition per call); once a second the busiest pairs go to coop.log.
/// The prologue (sub rsp,8 / mov r11d,edx / mov r8,rcx) has no RIP-relative code, so relocation is safe.
/// </summary>
public unsafe class XorProbe
{
    private const long RvaXorRange = 0x11AC2B0;
    private const int Slots = 16384;          // 16 bytes each: return address, state pointer

    private readonly CoopLog _log;
    private readonly byte* _buf;
    private readonly int* _idx;
    private readonly IAsmHook _hook;
    private readonly long _exe;

    public XorProbe(IReloadedHooks hooks, CoopLog log)
    {
        _log = log;
        _exe = (long)Process.GetCurrentProcess().MainModule!.BaseAddress;
        _buf = (byte*)NativeMemory.AllocZeroed(Slots * 16);
        _idx = (int*)NativeMemory.AllocZeroed(64);
        _hook = hooks.CreateAsmHook(new[]
        {
            "use64",
            "push rax", "push rdx", "push r9",
            $"mov r9, 0x{(ulong)_idx:X}",
            "mov eax, 1",
            "lock xadd dword [r9], eax",
            $"and eax, 0x{Slots - 1:X}",
            "shl rax, 4",
            $"mov rdx, 0x{(ulong)_buf:X}",
            "add rdx, rax",
            "mov r9, [rsp+24]",          // return address (3 pushes above it)
            "mov [rdx], r9",
            "mov [rdx+8], rcx",          // xorshift state pointer
            "pop r9", "pop rdx", "pop rax",
        }, _exe + RvaXorRange, AsmHookBehaviour.ExecuteFirst).Activate();
        new Thread(Report) { IsBackground = true, Name = "coop-xor-report" }.Start();
        log.Info($"Xorshift probe on at P3R.exe+0x{RvaXorRange:X}");
    }

    private void Report()
    {
        int last = 0;
        while (true)
        {
            Thread.Sleep(1000);
            int now = Volatile.Read(ref *_idx);
            if (now == last) continue;
            var hist = new Dictionary<(ulong, ulong), int>();
            for (int i = Math.Max(last, now - Slots); i < now; i++)
            {
                var e = _buf + (i & (Slots - 1)) * 16;
                var key = (*(ulong*)e, *(ulong*)(e + 8));
                hist[key] = hist.TryGetValue(key, out var c) ? c + 1 : 1;
            }
            var top = hist.OrderByDescending(kv => kv.Value).Take(8).Select(kv =>
            {
                var ret = (long)kv.Key.Item1 - _exe;
                var st = kv.Key.Item2;
                return $"P3R.exe+0x{ret:X} state=0x{st:X} x{kv.Value}";
            });
            _log.Quiet($"xor {now - last}/s: {string.Join(" | ", top)}");
            last = now;
        }
    }
}
