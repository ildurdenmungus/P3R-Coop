using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;

namespace p3rpc.coop;

public class Mod
{
    private readonly RngProbe? _rng;
    private readonly DungeonProbe? _dungeon;
    private readonly BattleHooks? _battle;
    private readonly XorProbe? _xor;

    public Mod(ILogger logger, IReloadedHooks hooks, Config config)
    {
        var log = new CoopLog(logger, config.LogDir);
        log.Info($"P3R Co-op {typeof(Mod).Assembly.GetName().Version} starting " +
                 $"(RngProbe={config.RngProbe}, DungeonProbe={config.DungeonProbe})");

        // Research probes stay off unless asked for: their managed hooks can fire while .NET is shutting down.
        if (config.RngProbe) _rng = new RngProbe(log);
        if (config.DungeonProbe && _rng != null) _dungeon = new DungeonProbe(hooks, log, _rng, config.ForceSeed);
        if (config.XorProbe) _xor = new XorProbe(hooks, log);
        if (config.BattleHooks) _battle = new BattleHooks(hooks, log, config.RunDir);
    }
}
