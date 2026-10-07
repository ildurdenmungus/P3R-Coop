using System.Diagnostics;

namespace p3rpc.coop;

/// <summary>
/// Field loot for both players (user, 2026-10-07: "chest loot not synced, sync breakables: when someone gets an
/// item everybody gets it"). Twice a second outside battles each game compares its inventory and money with the
/// last snapshot; what went UP (chests, pots, pickups) is sent as "ITEMS index:delta ... M:delta" and the partner
/// adds the same. Battles are left out (both games run them and get their drops themselves): the snapshot is
/// retaken a few seconds after each battle. Items used or sold are not mirrored.
///
/// UGlobalWork (Astrea layout): ItemBag +0x24E8 = byte counts per item (weapons .. outfits, 0x1D00 bytes),
/// Money (uint) +0x4260.
/// </summary>
public unsafe class ItemSync
{
    private const long RvaGetGlobalWork = 0x122AC90;
    private const long RvaGetFieldManager = 0x115A590;   // field state +0x280: 2/3 = player walking in the field
    private const int MaxGainItems = 6, MaxGainTotal = 30;  // more at once = a save load/shop, not loot
    private const int Bag = 0x24E8, BagCounts = 0x1D00, Money = 0x4260, MaxCount = 99;

    private readonly CoopLog _log;
    private readonly Session _session;
    private readonly Func<bool> _inBattle;
    private readonly delegate* unmanaged<nint> _getGlobalWork;
    private readonly delegate* unmanaged<nint> _getFieldManager;
    private readonly string _dir;
    private bool _fieldSeen;                           // tracking starts once we walked in the field
    private nint _gw;
    private byte[]? _base;
    private uint _baseMoney;
    private long _lastTick, _quietUntil;
    private bool _wasInBattle;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string[]> _incoming = new();

    public ItemSync(CoopLog log, Session session, string runDir, Func<bool> inBattle)
    {
        _dir = runDir;
        _log = log;
        _session = session;
        _inBattle = inBattle;
        var exe = (long)Process.GetCurrentProcess().MainModule!.BaseAddress;
        _getGlobalWork = (delegate* unmanaged<nint>)(exe + RvaGetGlobalWork);
        _getFieldManager = (delegate* unmanaged<nint>)(exe + RvaGetFieldManager);
    }

    public void OnMessage(string[] f)
    {
        if (f[0] == "ITEMS") _incoming.Enqueue(f);
        // The partner opened a chest / broke a pot: the Lua script removes the same one here.
        if (f[0] == "OBJ" && f.Length >= 4)
            try { File.AppendAllText(Path.Combine(_dir, "obj_remove.txt"), $"{f[1]} {f[2]} {f[3]}" + Environment.NewLine); } catch { }
    }

    /// <summary>Game thread (UWorld::Tick hook).</summary>
    public void Tick()
    {
        var now = Environment.TickCount64;
        if (now - _lastTick < 500) return;
        _lastTick = now;
        if (!_session.Connected) { _base = null; _fieldSeen = false; return; }
        // (2026-10-07: tracking from the title screen sent the whole inventory when the save loaded.)
        if (!_fieldSeen)
        {
            var gw = _getGlobalWork();
            if (gw == 0 || !Mem.Readable(gw + 0x24D4, 2) || *(ushort*)(gw + 0x24D4) == 0) return;   // no save loaded
            if (Mem.FieldState(_getFieldManager()) is not (2 or 3)) return;
            _fieldSeen = true;
            _base = null;
        }
        if (_inBattle()) { _wasInBattle = true; return; }
        if (_wasInBattle) { _wasInBattle = false; _base = null; _quietUntil = now + 4000; }   // results/drops land after the battle
        if (_gw == 0) _gw = _getGlobalWork();
        if (_gw == 0) return;
        var counts = (byte*)(_gw + Bag);
        var money = (uint*)(_gw + Money);

        if (_base == null || now < _quietUntil)
        {
            _base = new ReadOnlySpan<byte>(counts, BagCounts).ToArray();
            _baseMoney = *money;
            return;
        }

        // The partner's gains first; they go into the snapshot too, so they are not sent back.
        while (_incoming.TryDequeue(out var f))
        {
            for (int i = 1; i < f.Length; i++)
            {
                var kv = f[i].Split(':');
                if (kv.Length != 2 || !int.TryParse(kv[1], out var delta) || delta <= 0) continue;
                if (kv[0] == "M") { *money += (uint)delta; _baseMoney += (uint)delta; continue; }
                if (!int.TryParse(kv[0], out var idx) || idx < 0 || idx >= BagCounts) continue;
                var v = (byte)Math.Min(MaxCount, counts[idx] + delta);
                counts[idx] = v;
                _base[idx] = v;
            }
            _log.Info("items from the partner: " + string.Join(' ', f[1..]));
        }

        var gained = new List<string>();
        int total = 0;
        for (int i = 0; i < BagCounts; i++)
        {
            if (counts[i] > _base[i]) { gained.Add($"{i}:{counts[i] - _base[i]}"); total += counts[i] - _base[i]; }
            _base[i] = counts[i];
        }
        var items = gained.Count;
        if (*money > _baseMoney) gained.Add($"M:{*money - _baseMoney}");
        _baseMoney = *money;
        if (gained.Count == 0) return;
        if (items > MaxGainItems || total > MaxGainTotal)
        {
            _log.Info($"inventory jumped by {items} items ({total}) - a load or a shop, not sent");
            return;
        }
        _session.Send("ITEMS " + string.Join(' ', gained));
        // Tell the Lua script: the chest/pot next to the player is the source -> removed in the partner's game too.
        try { File.WriteAllText(Path.Combine(_dir, "item_gain.txt"), "1"); } catch { }
    }
}
