namespace p3rpc.coop;

/// <summary>
/// Link state and the control-message channel shared with P3RCoopLink. Files in RunDir:
///   peer.txt          (link rewrites every second) "host 1" / "guest 0": our role + whether the partner is
///                     connected. A file older than 3 s means the link is gone (= play alone).
///   net_out.txt       (we append)    control messages for the partner's game, one per line
///   net_in.txt        (link appends) control messages from the partner's game
///   remote_slots.txt  optional one-PC test override of the party slots the partner commands ("P2,P3")
/// Duos: the host commands P0 (MC) + P1, the guest P2 + P3.
/// </summary>
public class Session
{
    public enum Role { None, Host, Guest }

    private readonly string _dir;
    private readonly CoopLog _log;
    private long _inPos;
    private long _peerReadAt;
    private (Role Role, bool Connected) _peer;

    public Session(string dir, CoopLog log)
    {
        _dir = dir;
        _log = log;
        Directory.CreateDirectory(dir);
        try { File.Delete(Path.Combine(dir, "net_out.txt")); } catch { }
        // Messages that arrived while the game was closed belong to an old session.
        try { var p = Path.Combine(dir, "net_in.txt"); _inPos = File.Exists(p) ? new FileInfo(p).Length : 0; } catch { }
    }

    public Role MyRole { get { Refresh(); return _peer.Role; } }
    public bool Connected { get { Refresh(); return _peer.Connected; } }

    /// <summary>Party slots the partner commands in a joint battle (empty when playing alone).</summary>
    public HashSet<string> RemoteSlots()
    {
        var test = TestSlots();
        if (test.Count > 0) return test;
        if (!Connected) return new HashSet<string>();
        return MyRole == Role.Guest ? new HashSet<string> { "P0", "P1" } : new HashSet<string> { "P2", "P3" };
    }

    /// <summary>remote_slots.txt: one-PC tests without a partner game. Cached for a second: IsRemote() asks every
    /// frame while a partner turn is held, and a disk read per frame made the battle camera stutter.</summary>
    public HashSet<string> TestSlots()
    {
        var now = Environment.TickCount64;
        if (_testSlots != null && now - _testSlotsAt < 1000) return _testSlots;
        _testSlotsAt = now;
        try
        {
            var path = Path.Combine(_dir, "remote_slots.txt");
            _testSlots = File.Exists(path)
                ? File.ReadAllText(path).Split(new[] { ',', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet()
                : new HashSet<string>();
        }
        catch { _testSlots = new HashSet<string>(); }
        return _testSlots;
    }
    private HashSet<string>? _testSlots;
    private long _testSlotsAt;

    public void Send(string line, bool quiet = false)
    {
        try { File.AppendAllText(Path.Combine(_dir, "net_out.txt"), line + Environment.NewLine); } catch { }
        var shown = line.Length > 120 ? line[..120] + $"... ({line.Length} chars)" : line;   // MCPERSONA is ~1 KB of hex
        if (quiet) _log.Quiet("net -> " + shown); else _log.Info("net -> " + shown);
    }

    /// <summary>New messages from the partner since the last call (watcher thread).</summary>
    public List<string[]> Receive()
    {
        var list = new List<string[]>();
        var path = Path.Combine(_dir, "net_in.txt");
        try
        {
            if (!File.Exists(path)) { _inPos = 0; return list; }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _inPos) _inPos = 0;
            if (fs.Length == _inPos) return list;
            fs.Seek(_inPos, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd();
            var end = text.LastIndexOf('\n');
            if (end < 0) return list;                                   // half a line: wait for the rest
            _inPos += System.Text.Encoding.UTF8.GetByteCount(text[..(end + 1)]);
            foreach (var line in text[..end].Split('\n'))
            {
                var f = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length == 0) continue;
                if (f[0] is "HPSET" or "MCPERSONA" or "FLOORSEED" or "SHUFFLECUR") _log.Quiet("net <- " + (line.Length > 120 ? line[..120] + "..." : line.Trim())); else _log.Info("net <- " + line.Trim());
                list.Add(f);
            }
        }
        catch { }
        return list;
    }

    private void Refresh()
    {
        var now = Environment.TickCount64;
        if (now - _peerReadAt < 250) return;
        _peerReadAt = now;
        try
        {
            var path = Path.Combine(_dir, "peer.txt");
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(3))
            { _peer = (Role.None, false); return; }
            var f = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var role = f.Length > 0 && f[0] == "host" ? Role.Host : f.Length > 0 && f[0] == "guest" ? Role.Guest : Role.None;
            _peer = (role, role != Role.None && f.Length > 1 && f[1] == "1");
        }
        catch { /* keep the last value */ }
    }
}
