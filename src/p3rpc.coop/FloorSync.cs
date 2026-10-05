using System.Diagnostics;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p3rpc.coop;

/// <summary>
/// Same Tartarus floors on both games. The layout generator P3R.exe+0x119B140 (this, params) builds a 0x4938-byte
/// context on the stack (+0x119EE60) and seeds its xorshift128 state (+0x11AC370, seed ^ 0x0AED1A0C, ror 8 ...)
/// with the 16-bit value at params+2. That seed differs between two games that reached the floor differently
/// (first co-op session, 2026-10-05: "floors out of sync"), so while co-op is on, both games replace it with
/// hash(session seed, the floor fields of params). The host picks the session seed ("FLOORSEED n").
/// </summary>
public unsafe class FloorSync
{
    private const long RvaGenerateLayout = 0x119B140;

    [Function(CallingConventions.Microsoft)]
    public delegate nint GenerateLayoutFn(nint self, nint param);

    private readonly CoopLog _log;
    private readonly Session _session;
    private readonly IHook<GenerateLayoutFn> _hook;
    private uint _sessionSeed;                         // 0 = none yet
    private long _lastSent;

    public FloorSync(IReloadedHooks hooks, CoopLog log, Session session)
    {
        _log = log;
        _session = session;
        var exe = (long)Process.GetCurrentProcess().MainModule!.BaseAddress;
        _hook = hooks.CreateHook<GenerateLayoutFn>(GenerateLayoutImpl, exe + RvaGenerateLayout).Activate();
        log.Info("Floor sync on (layout generator seed)");
    }

    /// <summary>Watcher thread: the host announces its seed every few seconds (a guest may connect late).</summary>
    public void Tick()
    {
        if (!_session.Connected || _session.MyRole != Session.Role.Host) return;
        if (_sessionSeed == 0) _sessionSeed = (uint)Random.Shared.Next(1, int.MaxValue);
        var now = Environment.TickCount64;
        if (now - _lastSent < 5000) return;
        _lastSent = now;
        _session.Send($"FLOORSEED {_sessionSeed}", quiet: true);
    }

    public void OnMessage(string[] f)
    {
        if (f[0] != "FLOORSEED" || f.Length < 2 || _session.MyRole != Session.Role.Guest) return;
        var seed = uint.Parse(f[1]);
        if (seed != _sessionSeed) _log.Info($"floor seed from the host: {seed}");
        _sessionSeed = seed;
    }

    private nint GenerateLayoutImpl(nint self, nint param)
    {
        try
        {
            var w = (ushort*)param;
            var fields = $"{w[0]} {w[1]} {w[2]} {w[3]} {w[4]} {w[5]} {w[6]} {w[7]}";
            if (_sessionSeed != 0 && _session.Connected)
            {
                // The setup (+0x119EE60) copies words +0 and +8 next to the seed (ctx+0x490E / +0x1A1A): those identify
                // the floor. (The rest of the block can be stack junk; +0x10 is a pointer.)
                ulong x = _sessionSeed;
                x = (x ^ w[0]) * 0x100000001B3UL;
                x = (x ^ w[4]) * 0x100000001B3UL;
                x ^= x >> 29;
                var seed = (ushort)(x ^ (x >> 16) ^ (x >> 32));
                _log.Info($"floor layout: params [{fields}] seed {w[1]} -> {seed} (shared)");
                w[1] = seed;
            }
            else _log.Info($"floor layout: params [{fields}] (own seed)");
        }
        catch (Exception e) { _log.Error("GenerateLayout hook: " + e.Message); }
        return _hook.OriginalFunction(self, param);
    }
}
