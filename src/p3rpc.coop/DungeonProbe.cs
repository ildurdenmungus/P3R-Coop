using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p3rpc.coop;

/// <summary>
/// Floor-sync recon: hooks the native bodies of the Tartarus floor state-machine steps
/// (AFldDungeonGameInit::CREATE_LAYOUT etc., found via the UE native-function name table) and logs
/// how much rand() each one consumes on the game thread. With ForceSeed set, srand(ForceSeed) runs on the
/// game thread right before CREATE_LAYOUT so two runs can be compared for an identical layout.
/// Addresses are for the Aug 2025 Steam build; they become signatures once the approach is proven.
/// </summary>
public unsafe class DungeonProbe
{
    // exec thunk (from find_natives.py) -> jmp target = native implementation, all P3R.exe RVAs
    private const long RvaCreateLayout = 0x11A65D0;
    private const long RvaCreateEnd = 0x11A62C0;
    private const long RvaFloorInit = 0x11ACDE0;
    private const long RvaDungeonInitMaybe = 0x11AADE0;

    [Function(CallingConventions.Microsoft)]
    public delegate void StateFn(nint self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SrandNative(uint seed);

    private readonly CoopLog _log;
    private readonly RngProbe _rng;
    private readonly uint _forceSeed;
    private readonly SrandNative? _srand;
    private readonly List<object> _keepAlive = new();
    private int _layoutCount;

    public DungeonProbe(IReloadedHooks hooks, CoopLog log, RngProbe rng, uint forceSeed)
    {
        _log = log;
        _rng = rng;
        _forceSeed = forceSeed;
        if (rng.RealSrand != 0)
            _srand = Marshal.GetDelegateForFunctionPointer<SrandNative>(rng.RealSrand);

        var exe = (long)Process.GetCurrentProcess().MainModule!.BaseAddress;
        Hook(hooks, exe + RvaCreateLayout, "CREATE_LAYOUT", seedFirst: true);
        Hook(hooks, exe + RvaCreateEnd, "CREATE_END", seedFirst: false);
        Hook(hooks, exe + RvaFloorInit, "FloorInit", seedFirst: false);
        Hook(hooks, exe + RvaDungeonInitMaybe, "fn_11AADE0", seedFirst: false);
        log.Info($"Dungeon probe on (ForceSeed={forceSeed})");
    }

    private void Hook(IReloadedHooks hooks, long address, string name, bool seedFirst)
    {
        IHook<StateFn>? hook = null;
        StateFn impl = self =>
        {
            var n = seedFirst ? Interlocked.Increment(ref _layoutCount) : 0;
            var sw = Stopwatch.StartNew();
            var report = _rng.Measure(() =>
            {
                if (seedFirst && _forceSeed != 0) _srand?.Invoke(_forceSeed);
                hook!.OriginalFunction(self);
            });
            _log.Info($"{name}{(seedFirst ? $" #{n}" : "")} self=0x{self:X} {sw.Elapsed.TotalMilliseconds:F1} ms, {report}");
        };
        hook = hooks.CreateHook(impl, address).Activate();
        _keepAlive.Add(impl);
        _keepAlive.Add(hook);
    }
}
