using System.Diagnostics;

namespace p3rpc.coop;

/// <summary>
/// HP/SP of the whole party outside battles, both ways. Healing from the camp menu (spells, items) can target any
/// party member and costs the caster SP (user, 2026-10-07: "sync heal spells from the menu"), so each game watches
/// all four: a value that changed here since the last agreed one is sent ("HPSET id hp sp ..."), received values
/// are written and become the agreed ones (no echo). The host sends everyone once when the field is reached, so
/// both games start equal. Inside battles the lockstep keeps them equal; afterwards both report the same results.
///
/// UGlobalWork (p3rpc.nativetypes, Astrea-era layout = the Aug 2025 build): +0x1B0 FDatUnitWork[13] (0x2B4 each:
/// ID +0x8, Status.Hp +0xC, Status.Sp +0x10), +0x24D4 ushort ActiveCharacters[10] = party order by character id
/// (1 = MC). (The older layout's +0x1F6C read zeros on this build.)
/// </summary>
public unsafe class PartySync
{
    private const long RvaGetGlobalWork = 0x122AC90;   // p3rpc.nativetypes GetUGlobalWork signature
    private const long RvaGetFieldManager = 0x115A590;  // field state +0x280: 2/3 = walking in the field
    private const int Units = 0x1B0, UnitCount = 13, UnitSize = 0x2B4, UnitId = 0x8, UnitHp = 0xC, UnitSp = 0x10, Active = 0x24D4;

    private readonly CoopLog _log;
    private readonly Session _session;
    private readonly Func<bool> _inBattle;
    private readonly delegate* unmanaged<nint> _getGlobalWork;
    private nint _gw;
    private long _lastTick, _lastSend;
    private string? _lastSent;
    private long _diagAt = -60000;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string[]> _pending = new();   // HPSET from the partner
    private readonly Dictionary<int, (int Hp, int Sp)> _known = new();   // last agreed HP/SP per character id
    private bool _sentAll;                             // host: full party sent once
    private long _fieldSince;                          // when we were first seen walking in the field (0 = not yet)
    private delegate* unmanaged<nint> _getFieldManager;
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
        if (f[0] == "HPSET") _pending.Enqueue(f);
        if (f[0] == "MCPERSONA" && f.Length > 1) _pendingPersona = f[1];
    }

    /// <summary>Game thread, every frame (UWorld::Tick hook).</summary>
    public void Tick()
    {
        var now = Environment.TickCount64;
        if (now - _lastTick < 1000) return;
        _lastTick = now;
        if (!_session.Connected) { _sentAll = false; _fieldSince = 0; return; }   // a (re)connecting guest gets the full party again
        if (_inBattle()) return;
        if (_gw == 0) _gw = _getGlobalWork();
        if (_gw == 0) return;

        bool partyLoaded = *(ushort*)(_gw + Active) != 0;
        // A save load changes every value at once: say nothing until we have walked in the field for 3 s (the first
        // test sent the whole party right as the save loaded - a stale copy would overwrite the host's values).
        if (_fieldSince == 0)
        {
            if (_getFieldManager == null) _getFieldManager = (delegate* unmanaged<nint>)((long)Process.GetCurrentProcess().MainModule!.BaseAddress + RvaGetFieldManager);
            if (partyLoaded && Mem.FieldState(_getFieldManager()) is 2 or 3) _fieldSince = now;
        }
        bool settled = _fieldSince != 0 && now - _fieldSince > 3000;
        while (partyLoaded && _pending.TryDequeue(out var p))     // values wait while no save is loaded
        {
            for (int i = 1; i + 2 < p.Length; i += 3)
            {
                var id = int.Parse(p[i]);
                var unit = UnitById(id);
                if (unit == 0) continue;
                int hp = int.Parse(p[i + 1]), sp = int.Parse(p[i + 2]);
                *(int*)(unit + UnitHp) = hp;
                *(int*)(unit + UnitSp) = sp;
                _known[id] = (hp, sp);
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

        // Whatever changed here (menu heals, SP spent on them, items) goes to the partner.
        var msg = "HPSET";
        for (int pos = 0; pos < 4; pos++)
        {
            int id = *(ushort*)(_gw + Active + pos * 2);
            var unit = UnitById(id);
            if (unit == 0) continue;
            var cur = (*(int*)(unit + UnitHp), *(int*)(unit + UnitSp));
            if (!settled) { _known[id] = cur; continue; }   // loading: just remember
            bool first = !_known.ContainsKey(id);
            if (first) _known[id] = cur;
            if ((_session.MyRole == Session.Role.Host && !_sentAll) || (!first && _known[id] != cur))
            {
                _known[id] = cur;
                msg += $" {id} {cur.Item1} {cur.Item2}";
            }
        }
        if (_session.MyRole == Session.Role.Host && settled) _sentAll = true;
        if (msg == "HPSET" && now - _diagAt > 60000 && *(ushort*)(_gw + Active) == 0)
        {
            // Layout check: no party found (no save loaded yet, or the layout moved) -> dump what we read.
            _diagAt = now;
            var act = string.Join(",", Enumerable.Range(0, 10).Select(i => *(ushort*)(_gw + Active + i * 2)));
            _log.Info($"PartySync: GlobalWork 0x{_gw:X}, no party yet (active [{act}])");
        }
        if (msg == "HPSET") return;
        _session.Send(msg, quiet: true);
        _log.Quiet("HP/SP changed here: " + msg);
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
