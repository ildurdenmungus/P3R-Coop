using Reloaded.Mod.Interfaces;

namespace p3rpc.coop;

/// <summary>Writes to the Reloaded console and to a plain file the dev tools can tail.</summary>
public class CoopLog
{
    private readonly ILogger _logger;
    private readonly string? _filePath;
    private readonly object _lock = new();

    public CoopLog(ILogger logger, string dir)
    {
        _logger = logger;
        try
        {
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "coop.log");
            File.AppendAllText(_filePath, $"---- session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ----{Environment.NewLine}");
        }
        catch { _filePath = null; }
    }

    public void Info(string msg) => Write(msg, toConsole: true);
    /// <summary>File only; for high-volume lines that would flood the Reloaded console.</summary>
    public void Quiet(string msg) => Write(msg, toConsole: false);
    public void Error(string msg) => Write("ERROR " + msg, toConsole: true);

    private void Write(string msg, bool toConsole)
    {
        if (toConsole) _logger.WriteLine($"[P3R Co-op] {msg}");
        if (_filePath == null) return;
        lock (_lock)
        {
            try { File.AppendAllText(_filePath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}"); }
            catch { /* never let logging take the game down */ }
        }
    }
}
