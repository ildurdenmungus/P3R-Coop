using System.Diagnostics;

namespace p3rpc.coop;

/// <summary>
/// HP/SP of the duos outside battles. Each game owns its duo - host: party positions 0+1 (MC + first member),
/// guest: 2+3 - and sends their HP/SP once a second while not in a battle ("HPSP id hp sp id hp sp"); the partner
/// writes them into its UGlobalWork units. So joint battles start from the same values on both games; inside a
/// battle the lockstep keeps them equal and each battle writes its results back on both games.
///
/// UGlobalWork (p3rpc.nativetypes): +0x1B0 FDatUnitWork[11] (0x2B4 each: ID +0x8, Status.Hp +0xC, Status.Sp +0x10),
/// +0x1F6C ushort ActiveCharacters[10] = party order by character id (1 = MC).
/// </summary>
public unsafe class PartySync
{
    private const long RvaGetGlobalWork = 0x122AC90;   // p3rpc.nativetypes GetUGlobalWork signature
    private const int Units = 0x1B0, UnitSize = 0x2B4, UnitId = 0x8, UnitHp = 0xC, UnitSp = 0x10, Active = 0x1F6C;

    private readonly CoopLog _log;
    private readonly Session _session;
    private readonly Func<bool> _inBattle;
    private readonly delegate* unmanaged<nint> _getGlobalWork;
    private nint _gw;
    private long _lastTick, _lastSend;
    private string? _lastSent;
    private volatile string[]? _pending;               // latest HPSP from the partner, applied on the game thread

    public PartySync(CoopLog log, Session session, Func<bool> inBattle)
    {
        _log = log;
        _session = session;
        _inBattle = inBattle;
        _getGlobalWork = (delegate* unmanaged<nint>)((long)Process.GetCurrentProcess().MainModule!.BaseAddress + RvaGetGlobalWork);
    }

    public void OnMessage(string[] f)
    {
        if (f[0] == "HPSP") _pending = f;
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

        var own = _session.MyRole == Session.Role.Guest ? new[] { 2, 3 } : new[] { 0, 1 };
        var msg = "HPSP";
        foreach (var pos in own)
        {
            int id = *(ushort*)(_gw + Active + pos * 2);
            var unit = UnitById(id);
            if (unit != 0) msg += $" {id} {*(int*)(unit + UnitHp)} {*(int*)(unit + UnitSp)}";
        }
        if (msg == _lastSent && now - _lastSend < 5000) return;
        _lastSent = msg;
        _lastSend = now;
        _session.Send(msg, quiet: true);
    }

    private nint UnitById(int id)
    {
        if (id <= 0 || id > 11) return 0;
        var unit = _gw + Units + (id - 1) * UnitSize;
        if (*(int*)(unit + UnitId) == id) return unit;
        for (int i = 0; i < 11; i++)
        {
            var u = _gw + Units + i * UnitSize;
            if (*(int*)(u + UnitId) == id) return u;
        }
        return 0;
    }
}
