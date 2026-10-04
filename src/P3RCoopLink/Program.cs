using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

// P3RCoopLink: LAN link between two P3R co-op games.
// The in-game Lua script writes our state to <dir>/local.txt; we send it to the peer over UDP and write the
// peer's latest state to <dir>/remote.txt for the script to apply.
//
//   P3RCoopLink host            wait for a guest (announces itself on the LAN)
//   P3RCoopLink join [ip]       join a host (finds one on the LAN if no ip is given)
//   P3RCoopLink mirror          no network: echo our own state back 0.25 s late, offset sideways (one-PC test)

const int GamePort = 27777;
const int BeaconPort = 27778;
const string Magic = "P3RCOOP1";

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
var dir = Environment.GetEnvironmentVariable("P3RCOOP_DIR") ?? @"C:\Users\mbasd\P3R-Coop\run";
Directory.CreateDirectory(dir);
var localPath = Path.Combine(dir, "local.txt");
var remotePath = Path.Combine(dir, "remote.txt");
var statusPath = Path.Combine(dir, "link_status.txt");

void Status(string s)
{
    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {s}");
    try { File.WriteAllText(statusPath, s); } catch { }
}

string? ReadLocal()
{
    try
    {
        using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var line = sr.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
    }
    catch { return null; }
}

void WriteRemote(string line)
{
    // write-then-rename so the game never reads half a line
    var tmp = remotePath + ".tmp";
    try { File.WriteAllText(tmp, line + "\n"); File.Move(tmp, remotePath, overwrite: true); } catch { }
}

switch (mode)
{
    case "mirror": await Mirror(); break;
    case "bot": await Bot(); break;
    case "host": await RunPeer(isHost: true, hostIp: null); break;
    case "join": await RunPeer(isHost: false, hostIp: args.Length > 1 ? args[1] : null); break;
    default:
        Console.WriteLine("usage: P3RCoopLink host | join [ip] | mirror | bot");
        return;
}

async Task Mirror()
{
    Status("mirror mode: your own state comes back 0.25 s late, shifted 250 units");
    var history = new Queue<(long t, string line)>();
    var sw = Stopwatch.StartNew();
    while (true)
    {
        var line = ReadLocal();
        if (line != null) history.Enqueue((sw.ElapsedMilliseconds, line));
        while (history.Count > 0 && sw.ElapsedMilliseconds - history.Peek().t > 250)
        {
            var (_, old) = history.Dequeue();
            var f = old.Split(' ');
            // local line: seq floor x y z yaw   -> shift x so the ghost stands beside the path
            if (f.Length >= 6 && float.TryParse(f[2], out var x))
            {
                f[2] = (x + 250f).ToString("F0");
                WriteRemote(string.Join(' ', f));
            }
        }
        await Task.Delay(50);
    }
}

// One-PC battle test: stands in for the other player. When the game holds a remote unit's turn
// (cmd_request.txt = "b n slot"), answer 1.5 s later with a normal attack on the first enemy.
async Task Bot()
{
    Status("bot mode: answering remote battle turns with 'attack E0' after 1.5 s");
    var req = Path.Combine(dir, "cmd_request.txt");
    var outPath = Path.Combine(dir, "remote_cmds.txt");
    string? answered = null;
    while (true)
    {
        await Task.Delay(100);
        string? line = null;
        try { if (File.Exists(req)) line = File.ReadAllText(req).Trim(); } catch { }
        if (string.IsNullOrEmpty(line) || line == answered) continue;
        answered = line;
        await Task.Delay(1500);
        var cmd = $"{line} 1 0 E0";      // "b n slot" + type 1 (attack), id 0, target enemy 0
        File.AppendAllText(outPath, cmd + Environment.NewLine);
        Status($"answered: {cmd}");
    }
}

async Task RunPeer(bool isHost, string? hostIp)
{
    using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, isHost ? GamePort : 0));
    udp.Client.ReceiveTimeout = 0;
    IPEndPoint? peer = null;
    var lastHeard = Stopwatch.StartNew();
    var cts = new CancellationTokenSource();

    if (isHost)
    {
        _ = Task.Run(() => Beacon(cts.Token));
        Status($"hosting on UDP {GamePort}; waiting for a guest on the LANвЂ¦");
    }
    else
    {
        var ip = hostIp != null ? IPAddress.Parse(hostIp) : await FindHost();
        peer = new IPEndPoint(ip, GamePort);
        Status($"joining host {peer}вЂ¦");
    }

    // receive loop
    _ = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(cts.Token); } catch { continue; }
            var msg = Encoding.UTF8.GetString(r.Buffer);
            if (!msg.StartsWith(Magic + "|")) continue;
            var body = msg[(Magic.Length + 1)..];
            if (peer == null || !peer.Equals(r.RemoteEndPoint))
            {
                if (isHost && peer == null) Status($"guest connected from {r.RemoteEndPoint}");
                if (isHost) peer = r.RemoteEndPoint;
                else if (!peer!.Address.Equals(r.RemoteEndPoint.Address)) continue;
            }
            if (lastHeard.Elapsed.TotalSeconds > 3 && !isHost) Status($"connected to host {r.RemoteEndPoint}");
            lastHeard.Restart();
            if (body.StartsWith("S ")) WriteRemote(body[2..]);
        }
    });

    // send loop: our state at 20 Hz (a keepalive when the game hasn't written anything yet)
    var lastWarn = Stopwatch.StartNew();
    while (true)
    {
        if (peer != null)
        {
            var line = ReadLocal() ?? "idle";
            var bytes = Encoding.UTF8.GetBytes($"{Magic}|S {line}");
            try { await udp.SendAsync(bytes, peer); } catch { }
            if (lastHeard.Elapsed.TotalSeconds > 3 && lastWarn.Elapsed.TotalSeconds > 5)
            {
                Status(isHost ? "guest silent for 3 sвЂ¦" : "no answer from host yetвЂ¦");
                lastWarn.Restart();
            }
        }
        await Task.Delay(50);
    }
}

async Task Beacon(CancellationToken ct)
{
    using var b = new UdpClient { EnableBroadcast = true };
    var payload = Encoding.UTF8.GetBytes($"{Magic}|HOST|{Environment.MachineName}");
    while (!ct.IsCancellationRequested)
    {
        try { await b.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, BeaconPort)); } catch { }
        await Task.Delay(1000, ct);
    }
}

async Task<IPAddress> FindHost()
{
    Status($"looking for a host on the LAN (UDP {BeaconPort})вЂ¦");
    using var l = new UdpClient(new IPEndPoint(IPAddress.Any, BeaconPort));
    while (true)
    {
        var r = await l.ReceiveAsync();
        var msg = Encoding.UTF8.GetString(r.Buffer);
        if (msg.StartsWith(Magic + "|HOST|"))
        {
            Status($"found host '{msg.Split('|')[2]}' at {r.RemoteEndPoint.Address}");
            return r.RemoteEndPoint.Address;
        }
    }
}
