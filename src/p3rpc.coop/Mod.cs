using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;

namespace p3rpc.coop;

public class Mod
{
    private readonly RngProbe _rng;

    public Mod(ILogger logger, IReloadedHooks hooks, Config config)
    {
        var log = new CoopLog(logger, config.LogDir);
        log.Info($"P3R Co-op {typeof(Mod).Assembly.GetName().Version} starting (ForceSeed={config.ForceSeed})");
        _rng = new RngProbe(hooks, log, config.ForceSeed);
    }
}
