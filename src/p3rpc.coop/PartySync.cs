using System.Diagnostics;

namespace p3rpc.coop;

/// <summary>
/// HP/SP of the duos outside battles. Each game owns its duo - host: party positions 0+1 (MC + first member),
/// guest: 2+3 - and sends their HP/SP once a second while not in a battle ("HPSP id hp sp id hp sp"); the partner
/// writes them into its UGlobalWork units. So joint battles start from the same values on both games; inside a
/// battle the lockstep keeps them equal and each battle writes its results back on both games.
///
/// UGlobalWork (p3rpc.nativetypes, Astrea-era layout = the Aug 2025 build): +0x1B0 FDatUnitWork[13] (0x2B4 each:
/// ID +0x8, Status.Hp +0xC, Status.Sp +0x10), +0x24D4 ushort ActiveCharacters[10] = party order by character id
/// (1 = MC). (The older layout's +0x1F6C read zeros on this build.)
/// </summary>
public unsafe class PartySync
{
    private const long RvaGetGlobalWork = 0x122AC90;   // p3rpc.nativetypes GetUGlobalWork signature
    private const int Units = 0x1B0, UnitCount = 13, UnitSize = 0x2B4, UnitId = 0x8, UnitHp = 0xC, UnitSp = 0x10, Active = 0x24D4;

    private readonly CoopLog _log;
    private readonly Session _session;
    private readonly Func<bool> _inBattle;
    private readonly delegate* unmanaged<nint> _getGlobalWork;
    private nint _gw;
    private long _lastTick, _lastSend;
    private string? _lastSent;
    private long _diagAt = -60000;
    private volatile string[]? _pending;               // latest HPSP from the partner, applied on the game thread
    private volatile string? _pendingPersona;          // guest: the host's MC Persona block (hex), applied likewise
    private string? _lastPersona;
    private string? _lastOrder;
    private readonly string _runDir;
    private long _lastPersonaSent;
    // FDatUnitPersona = ushort equip + 12 stock entries of 0x30 (MC's Personas: ids, levels, skills, stats)
    private const int UnitPersona = 0x48, PersonaSize = 0x244;

    public PartySync(CoopLog log, Session session, string runDir, Func<bool> inBattle)
    {
        _runDir = runDir;
        _log = log;
        _session = session;
        _inBattle = inBattle;
        _getGlobalWork = (delegate* unmanaged<nint>)((long)Process.GetCurrentProcess().MainModule!.BaseAddress + RvaGetGlobalWork);
    }

    public void OnMessage(string[] f)
    {
        if (f[0] == "HPSP") _pending = f;
        if (f[0] == "MCPERSONA" && f.Length > 1) _pendingPersona = f[1];
    }

    /// <summary>Game thread, every frame (UWorld::Tick hook).</summary>
    public void Tick()
    {
        var now = Environment.TickCount64;
        if (now - _lastTick < 1000) return;
        _lastTick = now;
        if (!_session.Connected || _inBattle()) return;
        if (_gw == 0) _gw = _getGlobalWork();
        if (_gw == 0) return;

        if (_pending is { } p)
        {
            _pending = null;
            for (int i = 1; i + 2 < p.Length; i += 3)
            {
                var unit = UnitById(int.Parse(p[i]));
                if (unit == 0) continue;
                *(int*)(unit + UnitHp) = int.Parse(p[i + 1]);
                *(int*)(unit + UnitSp) = int.Parse(p[i + 2]);
            }
        }

        // MC's Personas belong to the host (fusions, levels, the equipped one): the guest copies them.
        if (_pendingPersona is { } hex && _session.MyRole == Session.Role.Guest && UnitById(1) is var mc && mc != 0)
        {
            _pendingPersona = null;
            var bytes = Convert.FromHexString(hex);
            if (bytes.Length == PersonaSize && !new ReadOnlySpan<byte>((void*)(mc + UnitPersona), PersonaSize).SequenceEqual(bytes))
            {
                fixed (byte* src = bytes) Buffer.MemoryCopy(src, (void*)(mc + UnitPersona), PersonaSize, PersonaSize);
                _log.Info($"PartySync: MC Personas copied from the host (equipped slot {*(short*)(mc + UnitPersona)})");
            }
        }
        if (_session.MyRole == Session.Role.Host && UnitById(1) is var hmc && hmc != 0)
        {
            var block = Convert.ToHexString(new ReadOnlySpan<byte>((void*)(hmc + UnitPersona), PersonaSize));
            if (block != _lastPersona || now - _lastPersonaSent > 10000)    // resend now and then: a late guest
            {
                _lastPersona = block;
                _lastPersonaSent = now;
                _session.Send("MCPERSONA " + block, quiet: true);
            }
        }

        // Party order for the Lua field script (the guest's own pawn takes the look of party slot 2).
        var order = string.Join(" ", Enumerable.Range(0, 4).Select(i => *(ushort*)(_gw + Active + i * 2)));
        if (order != _lastOrder)
        {
            _lastOrder = order;
            try { File.WriteAllText(Path.Combine(_runDir, "party.txt"), order); } catch { }
        }

        var own = _session.MyRole == Session.Role.Guest ? new[] { 2, 3 } : new[] { 0, 1 };
        var msg = "HPSP";
        foreach (var pos in own)
        {
            int id = *(ushort*)(_gw + Active + pos * 2);
            var unit = UnitById(id);
            if (unit != 0) msg += $" {id} {*(int*)(unit + UnitHp)} {*(int*)(unit + UnitSp)}";
        }
        if (msg == "HPSP" && now - _diagAt > 60000)
        {
            // Layout check: nothing found (no save loaded yet, or the layout moved) -> dump what we read.
            _diagAt = now;
            var act = string.Join(",", Enumerable.Range(0, 10).Select(i => *(ushort*)(_gw + Active + i * 2)));
            var units = string.Join(" | ", Enumerable.Range(0, UnitCount).Select(i =>
            {
                var u = _gw + Units + i * UnitSize;
                return $"{*(uint*)u:X} {*(ushort*)(u + 4)} {*(uint*)(u + 8)} hp{*(int*)(u + UnitHp)} sp{*(int*)(u + UnitSp)}";
            }));
            _log.Info($"PartySync: GlobalWork 0x{_gw:X}, active [{act}], units: {units}");
        }
        if (msg == _lastSent && now - _lastSend < 5000) return;
        _lastSent = msg;
        _lastSend = now;
        _session.Send(msg, quiet: true);
    }

    private nint UnitById(int id)
    {
        if (id <= 0 || id > UnitCount) return 0;
        var unit = _gw + Units + (id - 1) * UnitSize;
        if (*(int*)(unit + UnitId) == id) return unit;
        for (int i = 0; i < UnitCount; i++)
        {
            var u = _gw + Units + i * UnitSize;
            if (*(int*)(u + UnitId) == id) return u;
        }
        return 0;
    }
}
