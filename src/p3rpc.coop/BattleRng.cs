using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace p3rpc.coop;

/// <summary>
/// Same battle rolls on both games. Battle logic draws from the CRT rand() of the game thread, which per-frame
/// visual code also drains at a frame-rate-dependent pace, so two PCs would roll differently.
///
/// While armed, game-thread rand() calls whose call site lies in a battle code range get their value from a
/// private stream per call site, all seeded from one shared seed; everything else keeps the real rand().
/// A per-site stream only depends on how often *that* site was called, so cosmetic callers cannot shift the
/// battle's rolls. Calls coming through the generic RandRange helper (return address P3R.exe+0x11AC276) are
/// keyed by the helper's caller instead, so different users of the helper do not share a stream.
///
/// Arming is lazy: Arm(seed) only stores the seed; the first battle-site call after it resets every stream.
/// Battle code never runs outside battles, so arming a little before the battle starts is safe.
///
/// Mechanics: P3R.exe's IAT slot for rand → hand-assembled stub: if armed and on the game thread, call
/// Next(ret, ret2) (managed, UnmanagedCallersOnly), else jump to the previous slot target (real rand or the
/// RngProbe logging stub).
/// </summary>
public static unsafe class BattleRng
{
    private const ulong RandRangeHelperRet = 0x11AC276;     // rand call inside P3R.exe+0x11AC230 RandRange(n)

    // Battle code ranges (RVA). From the 2026-10-04 probe: battle logic call sites +0xFD261D..+0x1038FF4,
    // command functions +0x102FCE0/+0x10407F0; candidate enemy-AI/script sites +0x11EA2A9..+0x11ED49D.
    private static (ulong Lo, ulong Hi)[] _ranges = { (0xF80000, 0x1080000), (0x11E0000, 0x1200000) };

    private static CoopLog _log = null!;
    private static ulong _exe;
    private static byte* _armed;          // read by the stub
    private static ulong* _gameTid;       // read by the stub
    private static delegate* unmanaged[Cdecl]<int> _realRand;

    private static readonly object Lock = new();
    private static readonly Dictionary<ulong, uint> State = new();
    private static readonly Dictionary<ulong, int> Calls = new();
    private static uint _seed;
    private static bool _resetPending;

    public static bool Installed { get; private set; }

    public static void Install(CoopLog log)
    {
        _log = log;
        _exe = (ulong)Process.GetCurrentProcess().MainModule!.BaseAddress;
        var slot = ImportSlots.Find((byte*)_exe, "api-ms-win-crt-utility-l1-1-0.dll", "rand");
        if (slot == null) { log.Error("BattleRng: rand IAT slot not found"); return; }

        _armed = (byte*)NativeMemory.AllocZeroed(16);
        _gameTid = (ulong*)NativeMemory.AllocZeroed(16);
        *_gameTid = GetCurrentThreadId();       // Reloaded starts mods on the main (= UE game) thread; BattleHooks re-checks
        _realRand = (delegate* unmanaged[Cdecl]<int>)*slot;

        delegate* unmanaged[Cdecl]<ulong, ulong, int> next = &Next;
        var stub = ImportSlots.Emit(Stub((nint)next, *slot));
        ImportSlots.Patch(slot, stub);
        Installed = true;
        log.Info($"BattleRng on: rand slot P3R.exe+0x{(ulong)slot - _exe:X}, game thread {*_gameTid}");
    }

    /// <summary>Use these shared rolls for the next battle (lazy: resets on its first battle-site call).</summary>
    public static void Arm(uint seed)
    {
        if (!Installed) return;
        lock (Lock) { _seed = seed; _resetPending = true; }
        *_armed = 1;
        _log.Info($"BattleRng armed, seed {seed}");
    }

    public static void Disarm()
    {
        if (!Installed || *_armed == 0) return;
        *_armed = 0;
        lock (Lock)
        {
            var top = Calls.OrderByDescending(kv => kv.Value).Take(20).Select(kv => $"+0x{kv.Key:X} x{kv.Value}");
            _log.Info($"BattleRng disarmed; battle sites: {string.Join(" ", top)}");
        }
    }

    public static void SetGameThread(uint tid)
    {
        if (!Installed || *_gameTid == tid) return;
        _log.Info($"BattleRng: game thread is {tid} (was {*_gameTid})");
        *_gameTid = tid;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Next(ulong ret, ulong ret2)
    {
        var site = ret - _exe;
        if (site == RandRangeHelperRet) site = (ret2 - _exe) | 0x8000_0000_0000_0000UL;   // key by helper's caller
        if (!InBattleRange(site & 0x7FFF_FFFF_FFFF_FFFFUL)) return _realRand();

        lock (Lock)
        {
            if (_resetPending) { State.Clear(); Calls.Clear(); _resetPending = false; }
            if (!State.TryGetValue(site, out var s)) s = Mix(_seed, site);
            s = s * 214013u + 2531011u;                    // MSVC rand() LCG, one stream per call site
            State[site] = s;
            Calls[site] = Calls.TryGetValue(site, out var c) ? c + 1 : 1;
            return (int)((s >> 16) & 0x7FFF);
        }
    }

    private static bool InBattleRange(ulong rva)
    {
        foreach (var (lo, hi) in _ranges) if (rva >= lo && rva < hi) return true;
        return false;
    }

    private static uint Mix(uint seed, ulong site)
    {
        ulong x = seed ^ (site * 0x9E3779B97F4A7C15UL);
        x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL; x ^= x >> 33;
        return (uint)x;
    }

    // if (*armed && TEB.ThreadId == *gameTid) return Next([rsp], [rsp+0x30]); else jmp previous
    private static byte[] Stub(nint managedNext, nint previous)
    {
        var b = new List<byte>();
        b.AddRange(new byte[] { 0x48, 0xB8 }); b.AddRange(BitConverter.GetBytes((ulong)_armed));    // mov rax, armed
        b.AddRange(new byte[] { 0x80, 0x38, 0x00 });                                                 // cmp byte [rax], 0
        int jeAt = b.Count; b.AddRange(new byte[] { 0x74, 0x00 });                                   // je real
        b.AddRange(new byte[] { 0x31, 0xC0 });                                                       // xor eax, eax
        b.AddRange(new byte[] { 0x65, 0x48, 0x8B, 0x40, 0x48 });                                     // mov rax, gs:[rax+48h]
        b.AddRange(new byte[] { 0x48, 0xBA }); b.AddRange(BitConverter.GetBytes((ulong)_gameTid));  // mov rdx, gameTid
        b.AddRange(new byte[] { 0x48, 0x3B, 0x02 });                                                 // cmp rax, [rdx]
        int jneAt = b.Count; b.AddRange(new byte[] { 0x75, 0x00 });                                  // jne real
        b.AddRange(new byte[] { 0x48, 0x8B, 0x0C, 0x24 });                                           // mov rcx, [rsp]
        b.AddRange(new byte[] { 0x48, 0x8B, 0x54, 0x24, 0x30 });                                     // mov rdx, [rsp+30h]
        b.AddRange(new byte[] { 0x48, 0xB8 }); b.AddRange(BitConverter.GetBytes((ulong)managedNext)); // mov rax, Next
        b.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });                                           // sub rsp, 28h
        b.AddRange(new byte[] { 0xFF, 0xD0 });                                                       // call rax
        b.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });                                           // add rsp, 28h
        b.Add(0xC3);                                                                                 // ret
        int real = b.Count;
        b.AddRange(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }); b.AddRange(BitConverter.GetBytes((ulong)previous)); // jmp [rip]
        b[jeAt + 1] = (byte)(real - (jeAt + 2));
        b[jneAt + 1] = (byte)(real - (jneAt + 2));
        return b.ToArray();
    }

    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
}
