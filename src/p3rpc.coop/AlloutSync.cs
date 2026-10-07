using System.Diagnostics;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p3rpc.coop;

/// <summary>
/// All-Out Attack prompt in joint battles (user, 2026-10-08: "if someone agrees to do the all-out attack everyone
/// does it"). When every enemy is down both games show the hold-up prompt at the same point of the lockstep. The
/// prompt is Blueprint-driven and never reaches DoCommand (seen 2026-10-08: Go only set MC's AI to type 2 id 310),
/// so the turn relay can't carry the choice. The Blueprint calls three ABtlActor natives, hooked here:
///   StartHoldup      - the prompt opens (enemies surrounded)
///   DoGeneralAttack  - "Go" (総攻撃 = general attack = All-Out)
///   CancelHoldup     - "Cancel" (the unit gets its normal One More instead)
/// The first answer in either game decides for both: it is sent as "ALLOUT battle turn go|cancel" and the partner's
/// game, prompt open (or as soon as it opens), presses the same button through the battle input
/// (allout_force.txt -> BattleRecon.lua -> BtlInputComponent:CallInputTrigKeyboard). So a "Go" from either player
/// runs the All-Out in both games.
/// </summary>
public unsafe class AlloutSync
{
    private const long RvaStartHoldup = 0xFE4800;      // via exec thunk P3R.exe+0x15E6F20
    private const long RvaCancelHoldup = 0xFD8220;     // via exec thunk P3R.exe+0x15E35F0
    private const long RvaDoGeneralAttack = 0xFDC210;  // via exec thunk P3R.exe+0x15E4A20

    [Function(CallingConventions.Microsoft)]
    public delegate void ActorFn(nint actor);

    private readonly CoopLog _log;
    private readonly Session _session;
    private readonly string _dir;
    private readonly Func<int> _battle, _turn;   // lockstep battle id + DoCommand counter: equal in both games at the prompt
    private readonly Func<bool> _joint;
    private readonly Func<nint, string> _slot;
    private readonly IHook<ActorFn> _startHoldup, _cancelHoldup, _doGeneralAttack;

    private readonly object _lock = new();
    private (int Battle, int Turn)? _open;   // prompt open here
    private string? _forced;                 // decision we are pressing for the partner ("go"/"cancel")
    private readonly Dictionary<(int, int), string> _pending = new();   // partner answers that came before our prompt

    public AlloutSync(IReloadedHooks hooks, CoopLog log, Session session, string runDir,
                      Func<int> battle, Func<int> turn, Func<bool> joint, Func<nint, string> slot)
    {
        _log = log;
        _session = session;
        _dir = runDir;
        _battle = battle;
        _turn = turn;
        _joint = joint;
        _slot = slot;
        try { File.Delete(Path.Combine(_dir, "allout_force.txt")); } catch { }
        var exe = (long)Process.GetCurrentProcess().MainModule!.BaseAddress;
        _startHoldup = hooks.CreateHook<ActorFn>(StartHoldupImpl, exe + RvaStartHoldup).Activate();
        _cancelHoldup = hooks.CreateHook<ActorFn>(CancelHoldupImpl, exe + RvaCancelHoldup).Activate();
        _doGeneralAttack = hooks.CreateHook<ActorFn>(DoGeneralAttackImpl, exe + RvaDoGeneralAttack).Activate();
    }

    private void StartHoldupImpl(nint actor)
    {
        try { OnOpen(actor); } catch (Exception e) { _log.Error("StartHoldup hook: " + e.Message); }
        _startHoldup.OriginalFunction(actor);
    }

    private void CancelHoldupImpl(nint actor)
    {
        try { OnAnswer(actor, "cancel"); } catch (Exception e) { _log.Error("CancelHoldup hook: " + e.Message); }
        _cancelHoldup.OriginalFunction(actor);
    }

    private void DoGeneralAttackImpl(nint actor)
    {
        try { OnAnswer(actor, "go"); } catch (Exception e) { _log.Error("DoGeneralAttack hook: " + e.Message); }
        _doGeneralAttack.OriginalFunction(actor);
    }

    private void OnOpen(nint actor)
    {
        var key = (_battle(), _turn());
        _log.Info($"Battle {key.Item1} turn {key.Item2}: All-Out prompt for {_slot(actor)}");
        lock (_lock)
        {
            _open = key;
            // The partner already answered (its game got here first): press the same button here.
            if (_pending.Remove(key, out var what)) Force(what, "answered before our prompt opened");
        }
    }

    private void OnAnswer(nint actor, string what)
    {
        var b = _battle();
        lock (_lock)
        {
            var open = _open;
            _open = null;
            if (_forced != null)
            {
                if (_forced != what) _log.Error($"Battle {b}: All-Out pressed '{_forced}' for the partner but the game did '{what}' (key mapping?)");
                else _log.Info($"Battle {b}: All-Out '{what}' as the partner chose");
                _forced = null;
                try { File.Delete(Path.Combine(_dir, "allout_force.txt")); } catch { }
                return;
            }
            _log.Info($"Battle {b}: All-Out '{what}' chosen here ({_slot(actor)}{(open != null ? "" : ", no prompt seen")})");
            if (_joint() && open is { } o) _session.Send($"ALLOUT {o.Battle} {o.Turn} {what}");
        }
    }

    /// <summary>Background thread (link messages).</summary>
    public void OnMessage(string[] f)
    {
        if (f[0] != "ALLOUT" || f.Length < 4 || !int.TryParse(f[1], out var b) || !int.TryParse(f[2], out var n)) return;
        var what = f[3] == "go" ? "go" : "cancel";
        lock (_lock)
        {
            if (_open == (b, n)) Force(what, "prompt open here");
            else
            {
                _pending[(b, n)] = what;   // our game is behind: pressed when our prompt opens at the same point
                _log.Info($"Battle {b} turn {n}: partner chose All-Out '{what}' - waiting for our prompt");
            }
        }
    }

    // Caller holds _lock.
    private void Force(string what, string why)
    {
        _forced = what;
        try { File.WriteAllText(Path.Combine(_dir, "allout_force.txt"), what); } catch { }
        _log.Info($"Battle {_battle()}: partner chose All-Out '{what}' - pressing it here ({why})");
    }
}
