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
