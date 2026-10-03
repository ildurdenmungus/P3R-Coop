using System.Text.Json;

namespace p3rpc.coop;

/// <summary>User settings, read once at startup from Reloaded-II\User\Mods\p3rpc.coop\Config.json.</summary>
public class Config
{
    /// <summary>Non-zero: every srand() call in the game is given this seed instead (RNG recon test).</summary>
    public uint ForceSeed { get; set; }

    /// <summary>Folder for the recon logs (rng.log).</summary>
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
