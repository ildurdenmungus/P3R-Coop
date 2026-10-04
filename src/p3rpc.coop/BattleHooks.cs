using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p3rpc.coop;

/// <summary>
/// Battle command relay at the native level (UE4SS Lua hooks on these functions crashed the game).
///
/// Every command the game commits goes through UBtlActionManagerComponent::SetCommand and is executed by
/// DoCommand. Units are named by portable slots: P0-P3 = BtlCore.PlayerList index, E0.. = EnemyList index.
///
/// Files in RunDir (shared with the Lua game script and P3RCoopLink):
///   remote_slots.txt   which party slots the OTHER player controls, e.g. "P2,P3"
///   local_cmds.txt     (we append) commands of our own units:      "b n slot type id targets"
///   cmd_request.txt    (we write)  a remote unit is waiting:        "b n slot"
///   remote_cmds.txt    (link appends) commands from the other game: "b n slot type id targets"
///   kick.txt           (we write)  tells the Lua script to call DoCommand again once the command is here
///   battle_override.txt  manual test override: "P3 5 0 -" (applied every time, any battle/turn)
/// b = battle counter on this game, n = DoCommand counter within the battle (both games run the same
/// battle in lockstep, so the counters line up).
///
/// Remote unit's turn: DoCommand is NOT forwarded until the matching remote command has arrived; then the Lua
/// script calls DoCommand again, we write the remote command into the action manager + AI and let it run.
/// Offsets are from the UE4SS CXX dump of the Aug 2025 Steam build (see MODLOG "Battle").
/// </summary>
public unsafe class BattleHooks
{
    private const long RvaSetCommand = 0x10407F0;   // via exec thunk P3R.exe+0x15E63E0
    private const long RvaDoCommand = 0x102FCE0;    // via exec thunk P3R.exe+0x15E4A00
    private const long RvaSetCommandTargets = 0x1040820;   // via exec thunk P3R.exe+0x15E64F0 (TArray* = copy source)

    // UBtlActionManagerComponent
    private const int AmCommander = 0xC8, AmType = 0xD0, AmId = 0xD4, AmTargets = 0xD8;
    // ABtlActor
    private const int ActorAI = 0x3D0, ActorCore = 0x3E0;
    // UBtlCoreComponent
    private const int CorePlayers = 0x2F8, CoreEnemies = 0x308;
    // ABtlAI
    private const int AiType = 0xAF0, AiId = 0xAF4, AiSkill = 0xAF8, AiItem = 0xAFC, AiTargets = 0xB08;

    [Function(CallingConventions.Microsoft)]
    public delegate void SetCommandFn(nint am, nint commander, byte type, int id);

    [Function(CallingConventions.Microsoft)]
    public delegate void DoCommandFn(nint am);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetCommandTargetsNative(nint am, nint targetsArray);

    private record Command(int Battle, int Turn, string Slot, byte Type, int Id, string[] Targets);

    private readonly CoopLog _log;
    private readonly string _dir;
    private readonly IHook<SetCommandFn> _setCommand;
    private readonly IHook<DoCommandFn> _doCommand;
    private readonly SetCommandTargetsNative _setCommandTargets;

    private nint _core;                 // BtlCoreComponent of the current battle
    private int _battle, _turn;
    private (int Battle, int Turn, string Slot)? _waiting;
    private readonly ConcurrentDictionary<(int, int), Command> _received = new();
    private long _remoteReadPos;
    private (int Battle, int Turn)? _kicked;          // kick.txt is written once per held turn
    private nint _lastRemoteActor;                    // duplicate guard: actor + time of the last remote run
    private long _lastRemoteTicks;

    public BattleHooks(IReloadedHooks hooks, CoopLog log, string runDir)
    {
        _log = log;
        _dir = runDir;
        Directory.CreateDirectory(runDir);
        Delete("kick.txt"); Delete("cmd_request.txt"); Delete("remote_cmds.txt"); Delete("local_cmds.txt");   // stale commands from an earlier session would be replayed

        var exe = (long)Process.GetCurrentProcess().MainModule!.BaseAddress;
        _setCommand = hooks.CreateHook<SetCommandFn>(SetCommandImpl, exe + RvaSetCommand).Activate();
        _doCommand = hooks.CreateHook<DoCommandFn>(DoCommandImpl, exe + RvaDoCommand).Activate();
        _setCommandTargets = Marshal.GetDelegateForFunctionPointer<SetCommandTargetsNative>((nint)(exe + RvaSetCommandTargets));
        new Thread(WatchRemote) { IsBackground = true, Name = "coop-battle-remote" }.Start();
        log.Info("Battle hooks on (SetCommand, DoCommand, remote relay)");
    }

    private void SetCommandImpl(nint am, nint commander, byte type, int id)
    {
        _setCommand.OriginalFunction(am, commander, type, id);
        try { _log.Quiet($"SetCommand {Slot(commander)} type={type} id={id}"); }
        catch { /* never throw into the game */ }
    }

    private void DoCommandImpl(nint am)
    {
        bool run = true;
        try { run = OnDoCommand(am); }
        catch (Exception e) { _log.Error("DoCommand hook: " + e.Message); }
        if (run) _doCommand.OriginalFunction(am);
    }

    /// <returns>false = hold the command (remote unit, its command has not arrived yet)</returns>
    private bool OnDoCommand(nint am)
    {
        var commander = *(nint*)(am + AmCommander);
        if (commander == 0) return true;
        var core = *(nint*)(commander + ActorCore);
        if (core != _core) { _core = core; _battle++; _turn = 0; _waiting = null; _received.Clear(); }
        var slot = Slot(commander);

        // A second DoCommand for the unit whose remote command just ran is a duplicate (a late kick): drop it.
        if (_waiting == null && commander == _lastRemoteActor && Stopwatch.GetTimestamp() - _lastRemoteTicks < Stopwatch.Frequency * 3 / 10)
        {
            _log.Info($"Battle {_battle}: ignored duplicate DoCommand for {slot}");
            return false;
        }

        // Re-entry from the Lua kick: same turn we were holding.
        bool resumed = _waiting is { } w && w.Battle == _battle && w.Slot == slot;
        if (!resumed) _turn++;

        if (ManualOverride(am, commander, slot)) { LogCmd("DoCommand", am, commander, slot, " (OVERRIDDEN)"); return true; }

        if (!RemoteSlots().Contains(slot) || !slot.StartsWith('P'))
        {
            LogCmd("DoCommand", am, commander, slot, "");
            if (slot.StartsWith('P'))
                Append("local_cmds.txt", $"{_battle} {_turn} {slot} {*(byte*)(am + AmType)} {*(int*)(am + AmId)} {Targets(commander, am + AmTargets)}");
            return true;
        }

        if (_received.TryRemove((_battle, _turn), out var cmd) && cmd.Slot == slot)
        {
            Apply(am, commander, cmd.Type, cmd.Id, cmd.Targets);
            _waiting = null;
            _lastRemoteActor = commander;
            _lastRemoteTicks = Stopwatch.GetTimestamp();
            Delete("cmd_request.txt");
            Delete("kick.txt");
            LogCmd("DoCommand", am, commander, slot, $" (REMOTE b{cmd.Battle} n{cmd.Turn})");
            return true;
        }

        if (!resumed)
        {
            _waiting = (_battle, _turn, slot);
            Write("cmd_request.txt", $"{_battle} {_turn} {slot}");
            _log.Info($"Battle {_battle} turn {_turn}: holding {slot} until the other player's command arrives");
        }
        return false;
    }

    // Background: pick up remote commands as P3RCoopLink appends them; kick the game when the held one is here.
    private void WatchRemote()
    {
        var path = Path.Combine(_dir, "remote_cmds.txt");
        while (true)
        {
            Thread.Sleep(50);
            try
            {
                if (!File.Exists(path)) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _remoteReadPos) _remoteReadPos = 0;   // file was reset
                fs.Seek(_remoteReadPos, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 6 || !int.TryParse(f[0], out var b) || !int.TryParse(f[1], out var n)) continue;
                    var cmd = new Command(b, n, f[2], byte.Parse(f[3]), int.Parse(f[4]), f[5] == "-" ? Array.Empty<string>() : f[5].Split(','));
                    _received[(b, n)] = cmd;
                    _log.Quiet($"remote cmd b{b} n{n} {cmd.Slot} type={cmd.Type} id={cmd.Id} targets={f[5]}");
                }
                _remoteReadPos = fs.Position;
                if (_waiting is { } w && _received.ContainsKey((w.Battle, w.Turn)) && _kicked != (w.Battle, w.Turn))
                {
                    _kicked = (w.Battle, w.Turn);
                    Write("kick.txt", $"{w.Battle} {w.Turn}");
                }
            }
            catch { /* file busy: next round */ }
        }
    }

    private bool ManualOverride(nint am, nint commander, string slot)
    {
        var path = Path.Combine(_dir, "battle_override.txt");
        if (!File.Exists(path)) return false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3 || f[0] != slot) continue;
            Apply(am, commander, byte.Parse(f[1]), int.Parse(f[2]), f.Length > 3 && f[3] != "-" ? f[3].Split(',') : Array.Empty<string>());
            return true;
        }
        return false;
    }

    // Write a command the way the game does: SetCommand (type/id) + SetCommandTargets with a temporary TArray —
    // the game copies it into the action manager with its own allocator. (Rewriting the manager's TArray in
    // place could leave an attack with zero targets when the local pick had none -> crash, 2026-10-04.)
    private void Apply(nint am, nint commander, byte type, int id, string[] targetSlots)
    {
        var targets = new List<nint>();
        foreach (var t in targetSlots) { var p = FromSlot(commander, t); if (p != 0) targets.Add(p); }

        _setCommand.OriginalFunction(am, commander, type, id);

        var tmp = (nint*)NativeMemory.AllocZeroed(16);
        var buf = (nint*)NativeMemory.AllocZeroed((nuint)Math.Max(1, targets.Count) * 8);
        try
        {
            for (int i = 0; i < targets.Count; i++) buf[i] = targets[i];
            tmp[0] = (nint)buf;
            ((int*)tmp)[2] = targets.Count;   // ArrayNum
            ((int*)tmp)[3] = targets.Count;   // ArrayMax
            _setCommandTargets(am, (nint)tmp);
        }
        finally { NativeMemory.Free(buf); NativeMemory.Free(tmp); }

        // Mirror into the unit's AI decision fields (read by UI/logic); targets there only shrink in place.
        var ai = *(nint*)(commander + ActorAI);
        if (ai == 0) return;
        *(byte*)(ai + AiType) = type;
        *(uint*)(ai + AiId) = (uint)id;
        if (type == 2) *(uint*)(ai + AiSkill) = (uint)id;
        if (type == 3) *(uint*)(ai + AiItem) = (uint)id;
        if (targets.Count <= *(int*)(ai + AiTargets + 12)) SetTargets(ai + AiTargets, targets);
    }
    // TArray<ABtlActor*> = { T* data; int num; int max }. Rewrite in place only (no allocation from here):
    // shrink freely, grow only up to the existing capacity.
    private static void SetTargets(nint arr, List<nint> targets)
    {
        var data = *(nint**)arr;
        var max = *(int*)(arr + 12);
        var n = Math.Min(targets.Count, data == null ? 0 : max);
        for (int i = 0; i < n; i++) data[i] = targets[i];
        *(int*)(arr + 8) = n;
    }

    private HashSet<string> RemoteSlots()
    {
        try
        {
            var path = Path.Combine(_dir, "remote_slots.txt");
            return File.Exists(path)
                ? File.ReadAllText(path).Split(new[] { ',', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet()
                : new HashSet<string>();
        }
        catch { return new HashSet<string>(); }
    }

    private void LogCmd(string what, nint am, nint commander, string slot, string suffix) =>
        _log.Quiet($"{what} b{_battle} n{_turn} {slot} type={*(byte*)(am + AmType)} id={*(int*)(am + AmId)} " +
                   $"targets={Targets(commander, am + AmTargets)}{suffix}");

    private string Slot(nint actor)
    {
        if (actor == 0) return "none";
        var core = *(nint*)(actor + ActorCore);
        if (core == 0) return $"0x{actor:X}";
        var i = IndexIn(core + CorePlayers, actor);
        if (i >= 0) return $"P{i}";
        i = IndexIn(core + CoreEnemies, actor);
        return i >= 0 ? $"E{i}" : $"0x{actor:X}";
    }

    private static nint FromSlot(nint anyActor, string slot)
    {
        var core = *(nint*)(anyActor + ActorCore);
        if (core == 0 || slot.Length < 2 || !int.TryParse(slot[1..], out var i)) return 0;
        var arr = core + (slot[0] == 'P' ? CorePlayers : CoreEnemies);
        var data = *(nint**)arr;
        var num = *(int*)(arr + 8);
        return data != null && i >= 0 && i < num ? data[i] : 0;
    }

    private static int IndexIn(nint arr, nint actor)
    {
        var data = *(nint**)arr;
        var num = *(int*)(arr + 8);
        if (data == null || num < 0 || num > 64) return -1;
        for (int i = 0; i < num; i++) if (data[i] == actor) return i;
        return -1;
    }

    private string Targets(nint commander, nint arr)
    {
        var data = *(nint**)arr;
        var num = *(int*)(arr + 8);
        if (data == null || num <= 0 || num > 16) return "-";
        var parts = new string[num];
        for (int i = 0; i < num; i++) parts[i] = data[i] == 0 ? "nil" : Slot(data[i]);
        return string.Join(",", parts);
    }

    private void Write(string file, string text) { try { File.WriteAllText(Path.Combine(_dir, file), text); } catch { } }
    private void Append(string file, string line) { try { File.AppendAllText(Path.Combine(_dir, file), line + Environment.NewLine); } catch { } }
    private void Delete(string file) { try { File.Delete(Path.Combine(_dir, file)); } catch { } }
}
