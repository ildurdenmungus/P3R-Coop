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
///   local_cmds.txt     (we append) commands of our own units:      "b n slot type id targets"
///   cmd_request.txt    (we write)  a remote unit is waiting:        "b n slot"
///   remote_cmds.txt    (link appends) commands from the other game: "b n slot type id targets"
///   battle_coop.txt    (we write)  "b slots": the current joint battle and the partner's slots (for Lua)
///   kick.txt           (we write)  emergency fallback: the Lua script calls DoCommand again
///   battle_override.txt  manual test override: "P3 5 0 -" (applied every time, any battle/turn)
/// b = shared battle id (EncounterSync: the starter's id; local battles get negative ids), n = DoCommand
/// counter within the battle (both games run the same battle in lockstep, so the counters line up).
/// The partner's slots come from Session (host P0+P1 / guest P2+P3), only in joint battles.
///
/// Remote unit's turn: DoCommand is NOT forwarded until the matching remote command has arrived; then the
/// world-tick hook calls it again, we write the remote command into the action manager + AI and let it run.
/// Offsets are from the UE4SS CXX dump of the Aug 2025 Steam build (see MODLOG "Battle").
/// </summary>
public unsafe class BattleHooks
{
    private const long RvaSetCommand = 0x10407F0;   // via exec thunk P3R.exe+0x15E63E0
    private const long RvaDoCommand = 0x102FCE0;    // via exec thunk P3R.exe+0x15E4A00
    private const long RvaFloorInit = 0x11ACDE0;    // AFldDungeonGameInit::FloorInit: game thread, runs before any Tartarus battle
    private const long RvaWorldTick = 0x31EA3A0;    // UWorld::Tick(this, ELevelTick, float dt): per frame, game thread, before
                                                    // actors tick (found via its "WorldTickMisc" CSV label). NOT
                                                    // UGameEngine::Tick (+0x30FE4A0): UE4SS hooks that one itself ->
                                                    // hooking it too gave startup crashes/stalls (2026-10-05).
    // Not hooked (2026-10-05): ABtlAI update P3R.exe+0xFC6F60 only runs for ENEMY units, and +0x1274CB0 is the
    // flowscript interpreter (returns 1 = yielded, 2 = finished) - hooking it slowed startup to ~80 s and
    // coincided with two UE4SS startup crashes.
    private const long RvaResizeForCopy = 0x950CF0;        // TArray<T*>::ResizeForCopy(this, NewMax, PrevMax) -> FMemory::Realloc
    private const long RvaSetCommandTargets = 0x1040820;   // via exec thunk P3R.exe+0x15E64F0 (TArray* = copy source)

    // UBtlActionManagerComponent
    private const int AmCommander = 0xC8, AmType = 0xD0, AmId = 0xD4, AmTargets = 0xD8;
    // ABtlActor
    private const int ActorAI = 0x3D0, ActorCore = 0x3E0;
    // UBtlCoreComponent
    private const int CorePlayers = 0x2F8, CoreEnemies = 0x308;
    // ABtlAI
    private const int AiType = 0xAF0, AiId = 0xAF4, AiSkill = 0xAF8, AiItem = 0xAFC, AiTargets = 0xB08;
    private const int AiCharacter = 0xAE0, AiScript = 0xB70;

    [Function(CallingConventions.Microsoft)]
    public delegate void SetCommandFn(nint am, nint commander, byte type, int id);

    [Function(CallingConventions.Microsoft)]
    public delegate void DoCommandFn(nint am);

    [Function(CallingConventions.Microsoft)]
    public delegate void FloorInitFn(nint self);

    [Function(CallingConventions.Microsoft)]
    public delegate void WorldTickFn(nint world, int tickType, float deltaSeconds);


    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetCommandTargetsNative(nint am, nint targetsArray);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResizeForCopyNative(nint array, int newMax, int prevMax);

    private record Command(int Battle, int Turn, string Slot, byte Type, int Id, string[] Targets);

    private readonly CoopLog _log;
    private readonly string _dir;
    private readonly IHook<SetCommandFn> _setCommand;
    private readonly IHook<DoCommandFn> _doCommand;
    private readonly IHook<FloorInitFn> _floorInit;
    private readonly IHook<WorldTickFn> _worldTick;

    private nint _heldAm;                      // action manager of the held remote turn (release target)
    private long _heldSince;                   // Stopwatch timestamp when the turn was held
    private long _arrivedAt;                   // when the held turn's command arrived (0 = not yet)
    private readonly SetCommandTargetsNative _setCommandTargets;
    private readonly ResizeForCopyNative _resizeForCopy;

    private nint _core;                 // BtlCoreComponent of the current battle
    private int _battle, _turn;
    private (int Battle, int Turn, string Slot)? _waiting;
    private readonly ConcurrentDictionary<(int, int), Command> _received = new();
    private long _remoteReadPos;
    private (int Battle, int Turn)? _kicked;          // kick.txt is written once per held turn
    private nint _lastRemoteActor;                    // duplicate guard: actor + time of the last remote run
    private long _lastRemoteTicks;

    private readonly uint _testSeed;
    private readonly Session _session;
    private readonly EncounterSync _encounter;
    private readonly PartySync _party;
    private volatile bool _inBattle;                  // from the first DoCommand to the Lua battle-end note
    private int _localBattles;
    private HashSet<string> _remote = new();          // partner's slots in the current battle

    public BattleHooks(IReloadedHooks hooks, CoopLog log, string runDir, Session session, EncounterSync encounter, uint battleRngTestSeed = 0)
    {
        _log = log;
        _dir = runDir;
        _session = session;
        _encounter = encounter;
        _party = new PartySync(log, session, () => _inBattle);
        _testSeed = battleRngTestSeed;
        if (_testSeed != 0) BattleRng.Arm(_testSeed);   // one-PC test: every battle uses this seed
        Directory.CreateDirectory(runDir);
        Delete("kick.txt"); Delete("battle_end.txt"); Delete("cmd_request.txt"); Delete("remote_cmds.txt"); Delete("local_cmds.txt");   // stale commands from an earlier session would be replayed
        Delete("battle_coop.txt");

        var exe = (long)Process.GetCurrentProcess().MainModule!.BaseAddress;
        _setCommand = hooks.CreateHook<SetCommandFn>(SetCommandImpl, exe + RvaSetCommand).Activate();
        _doCommand = hooks.CreateHook<DoCommandFn>(DoCommandImpl, exe + RvaDoCommand).Activate();
        _floorInit = hooks.CreateHook<FloorInitFn>(FloorInitImpl, exe + RvaFloorInit).Activate();
        _worldTick = hooks.CreateHook<WorldTickFn>(WorldTickImpl, exe + RvaWorldTick).Activate();
        _setCommandTargets = Marshal.GetDelegateForFunctionPointer<SetCommandTargetsNative>((nint)(exe + RvaSetCommandTargets));
        _resizeForCopy = Marshal.GetDelegateForFunctionPointer<ResizeForCopyNative>((nint)(exe + RvaResizeForCopy));
        new Thread(WatchRemote) { IsBackground = true, Name = "coop-battle-remote" }.Start();
        log.Info("Battle hooks on (SetCommand, DoCommand, world-tick release, remote relay)");
    }

    // Release point for held remote turns: the start of UWorld::Tick, once per frame on the game thread, before
    // any actor (or the battle) updates - nothing of the battle is half-way through anything here.
    // (The Lua kick ran inside an arbitrary UE4SS ProcessEvent callback: suspected cause of rare crashes.)
    private void WorldTickImpl(nint world, int tickType, float deltaSeconds)
    {
        try
        {
            _encounter.Tick();
            _party.Tick();
            if (_waiting is { } w && _heldAm != 0)
            {
                bool arrived = _received.ContainsKey((w.Battle, w.Turn));
                bool alone = !arrived && !IsRemote(w.Slot);       // partner could not join / link lost: play it here
                if (arrived || alone)
                {
                    var am = _heldAm;
                    _heldAm = 0;
                    _log.Info($"Battle {w.Battle} turn {w.Turn}: releasing {w.Slot} at world tick" + (alone ? " (partner gone: local command)" : ""));
                    DoCommandImpl(am);
                }
            }
        }
        catch (Exception e) { _log.Error("WorldTick hook: " + e.Message); }
        _worldTick.OriginalFunction(world, tickType, deltaSeconds);
    }

    // Tell BattleRng which thread is the game thread before the first battle on this floor rolls anything.
    private void FloorInitImpl(nint self)
    {
        try { BattleRng.SetGameThread(GetCurrentThreadId()); } catch { }
        _floorInit.OriginalFunction(self);
    }

    private void EnsureBattle(nint core)
    {
        if (core == _core) return;
        _core = core; _turn = 0; _waiting = null; _inBattle = true;
        var b = _encounter.Current;
        if (b != null) { b.CoreSeen = true; _battle = b.Id; }
        else _battle = -++_localBattles;                       // not shared with the partner
        // Commands of this battle may already be here (the partner's game started it first); drop older ones.
        foreach (var key in _received.Keys) if (key.Item1 != _battle) _received.TryRemove(key, out _);
        BattleRng.SetGameThread(GetCurrentThreadId());

        var test = _session.TestSlots();
        _remote = test.Count > 0 ? test : b != null ? _session.RemoteSlots() : new HashSet<string>();
        Write("battle_coop.txt", $"{_battle} {string.Join(",", _remote)}");
        _log.Info($"Battle {_battle} starts: " + (b == null ? "local" : b.Starter ? "we started it" : "joined the partner's") +
                  (_remote.Count > 0 ? $", partner commands {string.Join(",", _remote)}" : ""));
    }

    // A party slot the partner commands right now. A solo battle (partner could not join) or a lost link makes
    // every unit ours again; one-PC tests (remote_slots.txt) never go solo.
    private bool IsRemote(string slot)
    {
        if (!_remote.Contains(slot) || !slot.StartsWith('P')) return false;
        if (_session.TestSlots().Count > 0) return true;
        var b = _encounter.Current;
        return b != null && b.Id == _battle && !b.Solo && _session.Connected;
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
        EnsureBattle(*(nint*)(commander + ActorCore));
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

        if (!IsRemote(slot))
        {
            if (resumed) { _waiting = null; _heldAm = 0; Delete("cmd_request.txt"); }
            LogCmd("DoCommand", am, commander, slot, resumed ? " (held turn, played here)" : "");
            if (slot.StartsWith('P') && _remote.Count > 0)
                Append("local_cmds.txt", $"{_battle} {_turn} {slot} {*(byte*)(am + AmType)} {*(int*)(am + AmId)} {Targets(commander, am + AmTargets)}");
            return true;
        }

        if (_received.TryGetValue((_battle, _turn), out var other) && other.Slot != slot)
        {
            // The partner's game had another unit's turn here: the battles diverged. Stop the lockstep.
            _log.Error($"Battle {_battle} turn {_turn}: DESYNC (partner's turn {_turn} was {other.Slot}, ours is {slot}) -> playing alone");
            if (_encounter.Current is { } cur && cur.Id == _battle) cur.Solo = true;
            if (resumed) { _waiting = null; _heldAm = 0; Delete("cmd_request.txt"); }
            return true;
        }

        if (_received.TryRemove((_battle, _turn), out var cmd))
        {
            Apply(am, commander, cmd.Type, cmd.Id, cmd.Targets);
            _waiting = null;
            _heldAm = 0;
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
            _heldAm = am;
            _heldSince = Stopwatch.GetTimestamp();
            _arrivedAt = 0;
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
                foreach (var m in _session.Receive())
                {
                    try { _encounter.OnMessage(m); _party.OnMessage(m); } catch (Exception e) { _log.Error($"message '{string.Join(' ', m)}': {e.Message}"); }
                }
                // The Lua script reports the end of every battle: summarise the shared rolls and re-arm for the next.
                var end = Path.Combine(_dir, "battle_end.txt");
                if (File.Exists(end))
                {
                    File.Delete(end);
                    BattleRng.Disarm();
                    _encounter.OnBattleEnd();
                    _inBattle = false;
                    _core = 0;                                 // a new battle may reuse the same core object
                    Delete("battle_coop.txt");
                    if (_testSeed != 0) BattleRng.Arm(_testSeed);
                }
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
                // Emergency fallback only: the engine tick releases the turn on the next frame; if it somehow has
                // not 3 s after the command arrived, let the Lua script call DoCommand (the old path).
                if (_waiting is { } wa && _received.ContainsKey((wa.Battle, wa.Turn)) && _arrivedAt == 0)
                    _arrivedAt = Stopwatch.GetTimestamp();
                if (_waiting is { } w && _received.ContainsKey((w.Battle, w.Turn)) && _kicked != (w.Battle, w.Turn)
                    && _arrivedAt != 0 && Stopwatch.GetTimestamp() - _arrivedAt > Stopwatch.Frequency * 3)
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

        // The TArray parameter is by value: SetCommandTargets copies it and then FREES its data with the game's
        // allocator (MSVC: callee destroys by-value args). So the data must come from the game's allocator:
        // ResizeForCopy (P3R.exe+0x950CF0, what the exec thunk uses) allocates it; we only own the 16-byte header.
        var tmp = (nint*)NativeMemory.AllocZeroed(16);
        try
        {
            ((int*)tmp)[2] = targets.Count;                       // ArrayNum
            if (targets.Count > 0) _resizeForCopy((nint)tmp, targets.Count, 0);
            var data = (nint*)tmp[0];
            if (targets.Count > 0 && data == null) { _log.Error("ResizeForCopy returned no buffer"); return; }
            for (int i = 0; i < targets.Count; i++) data[i] = targets[i];
            _setCommandTargets(am, (nint)tmp);                    // copies, then frees tmp->data
        }
        finally { NativeMemory.Free(tmp); }

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

    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();

    private void Write(string file, string text) { try { File.WriteAllText(Path.Combine(_dir, file), text); } catch { } }
    private void Append(string file, string line) { try { File.AppendAllText(Path.Combine(_dir, file), line + Environment.NewLine); } catch { } }
    private void Delete(string file) { try { File.Delete(Path.Combine(_dir, file)); } catch { } }
}
