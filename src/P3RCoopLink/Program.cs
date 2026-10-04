using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

// P3RCoopLink: LAN link between two P3R co-op games.
// The in-game Lua script writes our state to <dir>/local.txt; we send it to the peer over UDP and write the
// peer's latest state to <dir>/remote.txt for the script to apply.
//
//   P3RCoopLink host [--save N] wait for a guest (announces itself on the LAN); --save N sends the host's save
//                               slot N to the guest (co-op always plays on the host's save: party, stats, items)
//   P3RCoopLink join [ip] [--slot N]  join a host (finds one on the LAN if no ip is given); the host's save is
//                               written to slot N (default: the first unused slot; an existing file is backed up)
//   P3RCoopLink mirror          no network: echo our own state back 0.25 s late, offset sideways (one-PC test)
//   P3RCoopLink bot [host|guest]  no network: plays the partner for a one-PC test. Our game gets the given role
//                               (default host), held battle turns are answered with "attack E0", partner battles
//                               are "joined"; write "start <encountId>" to bot_cmd.txt to make the fake partner
//                               start a battle that our game joins.
//   P3RCoopLink fakeguest <hostRunDir>   stand-in for the guest's GAME in a one-PC network test: when the host's
//                               game holds a turn, write the answer into OUR local_cmds.txt so our link sends it
//
// Over TCP (same port number as UDP): battle commands - each side tails its game's local_cmds.txt and appends
// what it receives to remote_cmds.txt (p3rpc.coop BattleHooks) - and control messages: net_out.txt +
// lua_out.txt -> peer's net_in.txt (p3rpc.coop Session/EncounterSync). peer.txt = "<role> <0|1>", rewritten every
// second; the mod treats an older file as "no partner".

const int GamePort = 27777;
const int BeaconPort = 27778;
const string Magic = "P3RCOOP1";

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
int? saveSlot = null, guestSlot = null;
var positional = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--save" && i + 1 < args.Length) saveSlot = int.Parse(args[++i]);
    else if (args[i] == "--slot" && i + 1 < args.Length) guestSlot = int.Parse(args[++i]);
    else positional.Add(args[i]);
}
string? Arg(int i) => i < positional.Count ? positional[i] : null;
var dir = Environment.GetEnvironmentVariable("P3RCOOP_DIR") ?? @"C:\Users\mbasd\P3R-Coop\run";
Directory.CreateDirectory(dir);
var localPath = Path.Combine(dir, "local.txt");
var remotePath = Path.Combine(dir, "remote.txt");
var statusPath = Path.Combine(dir, "link_status.txt");
var peerPath = Path.Combine(dir, "peer.txt");
var channelUp = false;

void WritePeer(string role, bool connected)
{
    try { File.WriteAllText(peerPath, $"{role} {(connected ? 1 : 0)}"); } catch { }
}

// Every new complete line of a file the game appends to (starts at `pos`).
List<string> Tail(string path, ref long pos)
{
    var lines = new List<string>();
    try
    {
        if (!File.Exists(path)) { pos = 0; return lines; }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < pos) pos = 0;                                     // the game reset the file
        if (fs.Length == pos) return lines;
        fs.Seek(pos, SeekOrigin.Begin);
        var buf = new byte[fs.Length - pos];
        var n = fs.Read(buf, 0, buf.Length);
        var text = Encoding.UTF8.GetString(buf, 0, n);
        var end = text.LastIndexOf('\n');
        if (end < 0) return lines;                                        // half a line: wait for the rest
        pos += Encoding.UTF8.GetByteCount(text[..(end + 1)]);
        foreach (var l in text[..end].Split('\n')) if (l.Trim().Length > 0) lines.Add(l.Trim());
    }
    catch { }
    return lines;
}

long EndOf(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; } }

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
    case "bot": await Bot(Arg(0) ?? "host"); break;
    case "fakeguest": await FakeGuest(Arg(0) ?? @"C:\Users\mbasd\P3R-Coop\run"); break;
    case "host": await RunPeer(isHost: true, hostIp: null); break;
    case "join": await RunPeer(isHost: false, hostIp: Arg(0)); break;
    default:
        Console.WriteLine("usage: P3RCoopLink host [--save N] | join [ip] [--slot N] | mirror | bot [host|guest] | fakeguest <hostRunDir>");
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

// One-PC battle test: stands in for the partner's game. Our game gets `role`; when it holds one of the partner's
// turns (cmd_request.txt = "b n slot"), answer 1.5 s later with a normal attack on the first enemy; battles our
// game starts are "joined" at once; "start <encountId>" in bot_cmd.txt makes the partner start one (we join).
async Task Bot(string role)
{
    Status($"bot mode: our game is the {role}; answering partner turns with 'attack E0' after 1.5 s");
    var req = Path.Combine(dir, "cmd_request.txt");
    var outPath = Path.Combine(dir, "remote_cmds.txt");
    var netIn = Path.Combine(dir, "net_in.txt");
    var netOut = Path.Combine(dir, "net_out.txt");
    var botCmd = Path.Combine(dir, "bot_cmd.txt");
    long netOutPos = EndOf(netOut);
    int nextId = 1001;
    // Key = content + write time: the game's battle/turn counters restart every launch, so the same "b n slot"
    // text comes back in a new session and must be answered again.
    string? answered = null;
    var beat = Stopwatch.StartNew();
    WritePeer(role, true);
    while (true)
    {
        await Task.Delay(100);
        if (beat.ElapsedMilliseconds > 1000) { WritePeer(role, true); beat.Restart(); }

        foreach (var line in Tail(netOut, ref netOutPos))
        {
            var f = line.Split(' ');
            if (f[0] == "BATTLE" && f.Length > 1)
            {
                File.AppendAllText(netIn, $"JOINED {f[1]}" + Environment.NewLine);
                Status($"our game started battle {f[1]}: partner joined");
            }
        }
        try
        {
            if (File.Exists(botCmd))
            {
                // "start <encount> [reason] [enemyId] [paramHex]" / "role host|guest"
                var c = File.ReadAllText(botCmd).Trim().Split(' ');
                File.Delete(botCmd);
                if (c[0] == "role" && c.Length > 1) { role = c[1]; WritePeer(role, true); Status($"our game is now the {role}"); }
                if (c[0] == "start" && c.Length > 1)
                {
                    var id = nextId++;
                    var msg = $"BATTLE {id} {Random.Shared.Next(1, int.MaxValue)} {(c.Length > 2 ? c[2] : "5")} {(c.Length > 3 ? c[3] : "-1")} {c[1]}";
                    File.AppendAllText(netIn, msg + Environment.NewLine);
                    if (c.Length > 4) File.AppendAllText(netIn, $"PARAM {id} {c[4]}" + Environment.NewLine);
                    Status($"partner starts a battle: {msg}" + (c.Length > 4 ? " + PARAM" : ""));
                }
            }
        }
        catch { }

        string? held = null;
        try { if (File.Exists(req)) held = File.ReadAllText(req).Trim() + "@" + File.GetLastWriteTimeUtc(req).Ticks; } catch { }
        if (string.IsNullOrEmpty(held) || held == answered) continue;
        answered = held;
        await Task.Delay(1500);
        var cmd = $"{held.Split('@')[0]} 1 0 E0";      // "b n slot" + type 1 (attack), id 0, target enemy 0
        File.AppendAllText(outPath, cmd + Environment.NewLine);
        Status($"answered: {cmd}");
    }
}

// One-PC network test: plays the guest GAME's part. The guest's real game would reach the same held turn by
// itself and write its unit's command to its own local_cmds.txt; here we watch the host's request instead.
async Task FakeGuest(string hostDir)
{
    Status($"fakeguest: answering the host game's held turns (watching {hostDir}) through our local_cmds.txt");
    var req = Path.Combine(hostDir, "cmd_request.txt");
    var outPath = Path.Combine(dir, "local_cmds.txt");
    var netIn = Path.Combine(dir, "net_in.txt");
    var netOut = Path.Combine(dir, "net_out.txt");
    long netInPos = EndOf(netIn);
    string? answered = null;
    while (true)
    {
        await Task.Delay(100);
        foreach (var l in Tail(netIn, ref netInPos))
        {
            var f = l.Split(' ');
            if (f[0] == "BATTLE" && f.Length > 1)
            {
                File.AppendAllText(netOut, $"JOINED {f[1]}" + Environment.NewLine);
                Status($"host started battle {f[1]}: guest game joined");
            }
        }
        string? line = null;
        try { if (File.Exists(req)) line = File.ReadAllText(req).Trim() + "@" + File.GetLastWriteTimeUtc(req).Ticks; } catch { }
        if (string.IsNullOrEmpty(line) || line == answered) continue;
        answered = line;
        await Task.Delay(1500);
        var cmd = $"{line.Split('@')[0]} 1 0 E0";
        File.AppendAllText(outPath, cmd + Environment.NewLine);
        Status($"guest game chose: {cmd}");
    }
}

// P3R saves: %APPDATA%\SEGA\P3R\Steam\<steam id>\SaveDataNNN.sav (slot N). Newest account folder wins.
string? SaveDir()
{
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SEGA", "P3R", "Steam");
    if (!Directory.Exists(root)) return null;
    return new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault()?.FullName;
}

// Host -> guest: "F <slot> <base64 of SaveDataNNN.sav>".
async Task SendSave(StreamWriter writer)
{
    if (saveSlot is not { } slot) return;
    var path = SaveDir() is { } d ? Path.Combine(d, $"SaveData{slot:000}.sav") : null;
    if (path == null || !File.Exists(path)) { Status($"save slot {slot} not found - nothing sent"); return; }
    await writer.WriteLineAsync($"F {slot} {Convert.ToBase64String(await File.ReadAllBytesAsync(path))}");
    Status($"sent the host's save (slot {slot}, {new FileInfo(path).Length / 1024} KB)");
}

void ReceiveSave(string body)
{
    var sp = body.IndexOf(' ');
    var bytes = Convert.FromBase64String(body[(sp + 1)..]);
    var d = SaveDir();
    if (d == null) { Status("no P3R save folder on this PC: start P3R once first"); return; }
    int slot = guestSlot ?? Enumerable.Range(1, 999).First(i => !File.Exists(Path.Combine(d, $"SaveData{i:000}.sav")));
    var path = Path.Combine(d, $"SaveData{slot:000}.sav");
    if (File.Exists(path))
    {
        var bak = path + $".coopbak-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, bak);
        Status($"backed up your slot {slot} to {Path.GetFileName(bak)}");
    }
    File.WriteAllBytes(path, bytes);
    Status($"HOST'S SAVE IS IN YOUR SLOT {slot}: load that slot in P3R ({bytes.Length / 1024} KB)");
}

// Reliable, ordered battle-command channel. Sends every new line of local_cmds.txt as "C <line>" and appends every
// received "C <line>" to remote_cmds.txt. Returns when the connection drops.
async Task CommandChannel(TcpClient tcp, CancellationToken ct)
{
    tcp.NoDelay = true;
    var stream = tcp.GetStream();
    var reader = new StreamReader(stream, Encoding.UTF8);
    var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    var localCmds = Path.Combine(dir, "local_cmds.txt");
    var remoteCmds = Path.Combine(dir, "remote_cmds.txt");
    var netOut = Path.Combine(dir, "net_out.txt");
    var luaOut = Path.Combine(dir, "lua_out.txt");
    var netIn = Path.Combine(dir, "net_in.txt");
    long pos = File.Exists(localCmds) ? new FileInfo(localCmds).Length : 0;   // only commands from now on
    long netPos = EndOf(netOut), luaPos = EndOf(luaOut);

    await SendSave(writer);
    var recv = Task.Run(async () =>
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (line.StartsWith("F "))
            {
                try { ReceiveSave(line[2..]); } catch (Exception e) { Status($"could not write the host's save: {e.Message}"); }
                continue;
            }
            if (line.StartsWith("M "))
            {
                File.AppendAllText(netIn, line[2..] + Environment.NewLine);
                Status($"received message: {line[2..]}");
                continue;
            }
            if (!line.StartsWith("C ")) continue;                                 // "H" = heartbeat
            File.AppendAllText(remoteCmds, line[2..] + Environment.NewLine);
            Status($"received command: {line[2..]}");
        }
    });

    while (!ct.IsCancellationRequested && !recv.IsCompleted)
    {
        try
        {
            if (File.Exists(localCmds))
            {
                using var fs = new FileStream(localCmds, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < pos) pos = 0;                                     // the game reset the file at startup
                if (fs.Length > pos)
                {
                    fs.Seek(pos, SeekOrigin.Begin);
                    using var sr = new StreamReader(fs);
                    string? l;
                    while ((l = await sr.ReadLineAsync()) != null)
                        if (l.Length > 0) { await writer.WriteLineAsync("C " + l); Status($"sent command: {l}"); }
                    pos = fs.Length;
                }
            }
            else pos = 0;
            foreach (var m in Tail(netOut, ref netPos).Concat(Tail(luaOut, ref luaPos)))
            {
                await writer.WriteLineAsync("M " + m);
                Status($"sent message: {m}");
            }
            await writer.WriteLineAsync("H");                                    // heartbeat (detects a dead peer)
        }
        catch (IOException) { break; }
        catch (ObjectDisposedException) { break; }
        await Task.Delay(50, ct);
    }
}

async Task RunCommandChannel(bool isHost, IPAddress? hostAddr, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            TcpClient tcp;
            if (isHost)
            {
                var listener = new TcpListener(IPAddress.Any, GamePort);
                listener.Start();
                try { tcp = await listener.AcceptTcpClientAsync(ct); } finally { listener.Stop(); }
            }
            else
            {
                tcp = new TcpClient();
                await tcp.ConnectAsync(hostAddr!, GamePort, ct);
            }
            Status($"battle channel connected ({tcp.Client.RemoteEndPoint})");
            channelUp = true;
            try { using (tcp) await CommandChannel(tcp, ct); }
            finally { channelUp = false; }
            Status("battle channel lost, reconnecting...");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { Status($"battle channel: {e.Message}"); }
        await Task.Delay(1000, ct);
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
        Status($"hosting on UDP {GamePort}; waiting for a guest on the LAN...");
    }
    else
    {
        var ip = hostIp != null ? IPAddress.Parse(hostIp) : await FindHost();
        peer = new IPEndPoint(ip, GamePort);
        Status($"joining host {peer}...");
    }
    _ = Task.Run(() => RunCommandChannel(isHost, peer?.Address, cts.Token));

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
    var beat = Stopwatch.StartNew();
    var role = isHost ? "host" : "guest";
    WritePeer(role, false);
    while (true)
    {
        if (beat.ElapsedMilliseconds > 1000) { WritePeer(role, channelUp); beat.Restart(); }
        if (peer != null)
        {
            var line = ReadLocal() ?? "idle";
            var bytes = Encoding.UTF8.GetBytes($"{Magic}|S {line}");
            try { await udp.SendAsync(bytes, peer); } catch { }
            if (lastHeard.Elapsed.TotalSeconds > 3 && lastWarn.Elapsed.TotalSeconds > 5)
            {
                Status(isHost ? "guest silent for 3 s..." : "no answer from host yet...");
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
    Status($"looking for a host on the LAN (UDP {BeaconPort})...");
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
