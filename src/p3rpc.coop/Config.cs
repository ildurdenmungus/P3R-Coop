using System.Text.Json;

namespace p3rpc.coop;

/// <summary>User settings, read once at startup from Reloaded-II\User\Mods\p3rpc.coop\Config.json.</summary>
public class Config
{
    /// <summary>Research only: log every CRT rand()/srand() call site (IAT stubs). Off by default.</summary>
    public bool RngProbe { get; set; }

    /// <summary>Research only: time the Tartarus floor state-machine steps (needs RngProbe). Off by default.</summary>
    public bool DungeonProbe { get; set; }

    /// <summary>With DungeonProbe: srand(ForceSeed) on the game thread before CREATE_LAYOUT (0 = off).</summary>
    public uint ForceSeed { get; set; }

    /// <summary>Research only: log callers of the xorshift128 helper P3R.exe+0x11AC2B0. Off by default.</summary>
    public bool XorProbe { get; set; }

    /// <summary>Shared battle rolls (BattleRng): per-call-site streams for battle code while armed. On by default
    /// (does nothing until armed by co-op or by BattleRngTestSeed).</summary>
    public bool BattleRng { get; set; } = true;

    /// <summary>One-PC test: non-zero = arm BattleRng with this seed at every battle.</summary>
    public uint BattleRngTestSeed { get; set; }

    /// <summary>Native battle command capture/override (BattleHooks). On by default.</summary>
    public bool BattleHooks { get; set; } = true;

    /// <summary>Folder shared with the game script and P3RCoopLink (battle_override.txt etc.).</summary>
    public string RunDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "P3R-Coop", "run");

    /// <summary>Folder for coop.log and the recon logs.</summary>
    public string LogDir { get; set; } = @"C:\Users\mbasd\P3R-Coop\recon";

    public static Config Load(string dir)
    {
        var path = Path.Combine(dir, "Config.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();

            Directory.CreateDirectory(dir);
            var fresh = new Config();
            File.WriteAllText(path, JsonSerializer.Serialize(fresh, new JsonSerializerOptions { WriteIndented = true }));
            return fresh;
        }
        catch
        {
            return new Config();
        }
    }
}
