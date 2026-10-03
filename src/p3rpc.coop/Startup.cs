using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace p3rpc.coop;

/// <summary>Reloaded-II entry point. Keeps loader plumbing out of the mod logic.</summary>
public class Startup : IMod
{
    private Mod? _mod;

    public void StartEx(IModLoaderV1 loaderApi, IModConfigV1 modConfig)
    {
        var modLoader = (IModLoader)loaderApi;
        var logger = (ILogger)modLoader.GetLogger();
        IReloadedHooks? hooks = null;
        modLoader.GetController<IReloadedHooks>()?.TryGetTarget(out hooks);
        if (hooks == null)
        {
            logger.WriteLine("[P3R Co-op] Reloaded.Hooks controller missing; mod disabled.", logger.ColorRed);
            return;
        }

        var configDir = modLoader.GetModConfigDirectory(modConfig.ModId);
        _mod = new Mod(logger, hooks, Config.Load(configDir));
    }

    public void Suspend() { }
    public void Resume() { }
    public void Unload() { }
    public bool CanUnload() => false;
    public bool CanSuspend() => false;
    public Action Disposing => () => { };
}
