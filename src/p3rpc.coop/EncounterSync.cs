using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p3rpc.coop;

/// <summary>
/// Joint battles: when either player's duo touches a Shadow, the partner's game enters the same battle.
///
/// Starter (Shadow touched here): the field asks for a battle through FieldManager::RequestEncount; we pick a
/// battle id + a BattleRng seed and send "BATTLE id seed reason enemyId encountId". When the battle sets up,
/// UBtlCoreComponent::SetEncountParam gives us the full FBtlEncountParam -> "PARAM id hex".
/// Partner: waits until its field is idle, then the Lua script calls AFldEnemyManager::ForceEncount; our hook
/// requests the battle with the Shadow of the same spawn id (its EncountID set to the starter's), arms the same
/// seed, overwrites its FBtlEncountParam with the starter's and answers "JOINED id" (or "BUSY id").
/// Both games then run the battle in lockstep (BattleHooks): battle id = the starter's id on both.
///
/// If the partner can't join (in a menu for 20 s, own battle, no Shadow on its floor), the starter's battle is
/// "solo": the starter's player commands all four.
///
/// Boss wait: the Lua field script keeps a Gatekeeper untouchable until the partner is near it too (Block Tales:
/// everyone at the boss). Its battle starts through Fuuka's prompt, not always through RequestEncount, so battles
/// that reach PreLoad unannounced are announced there. (HoldBoss/READY below = the earlier request-hold design.)
/// </summary>
public unsafe class EncounterSync
{
    private const long RvaRequestEncount = 0x11DB770;     // FieldManager::RequestEncount(this, FFldEnemyHolder*, int reason)
    private const long RvaSetEncountParam = 0x10408F0;    // UBtlCoreComponent::SetEncountParam native (BP; not called for Tartarus battles)
    // UBtlCoreComponent::LoadResidentData / LoadData / LoadEnemyData(this, const FBtlEncountParam&): each copies the
    // param into the core (+0x298) and loads that encounter's data (found by their writes to +0x298 and their
    // LoadedResidentData +0x418 / LoadedBattleData +0x419 / LoadedEnemyData +0x41B flags).
    private static readonly long[] RvaLoads = { 0x1036B50, 0x1035910, 0x10368D0 };
    // The field's battle start (AFldDungeonGame +0x11A6047..): param = UFldDungeonEncountController (control
    // component 0x12) +0x1A8, battle control = +0xFFA4F0(); then PreLoad(ctl, &param) +0x1000C60 (writes ctl+0x298,
    // sets +0x2BD) and Encount(ctl, &param) +0x10062E0. PreLoad is the earliest consumer: replacing the param there
    // makes the joiner load the starter's encounter (replacing it only in Load* left the boss assets unloaded).
    private const long RvaPreLoad = 0x1000C60, RvaEncount = 0x10062E0;
    private const long RvaEnemyForceEncount = 0x1130090;  // AFldEnemyManager::ForceEncount native (reason 5)
    private const long RvaGetFieldManager = 0x115A590;    // () -> field manager (game instance +0x100, else global)
    private const long RvaHolderObject = 0x115A210;       // (FFldEnemyHolder*) -> live enemy object or null

    // Field manager: pending encounter request {FFldEnemyHolder} + reason; state 2/3 = field is playable
    private const int MgrRequest = 0x290, MgrState = 0x280;
    // AFldEnemyManager.Enemies = TArray<FFldEnemyHolder{AActor* Actor; UFldEnemyComp* EnemyComp}>
    private const int EnemyArray = 0x278;
    // UFldEnemyComp
    private const int CompType = 0x118, CompId = 0x11C, CompEncount = 0x120;
    private const byte Gatekeeper = 4;                     // EFldEnemyType
    private const int ParamSize = 0x24;                    // FBtlEncountParam

    [Function(CallingConventions.Microsoft)]
    public delegate void RequestEncountFn(nint fieldMgr, nint holder, int reason);

    [Function(CallingConventions.Microsoft)]
    public delegate void SetEncountParamFn(nint core, nint param);

    [Function(CallingConventions.Microsoft)]
    public delegate void EnemyForceEncountFn(nint enemyMgr);

    [Function(CallingConventions.Microsoft)]
    public delegate nint LoadFn(nint core, nint param);

    public class Battle
    {
        public int Id;
        public uint Seed;
        public bool Starter;
        public volatile byte[]? Param;      // the starter's FBtlEncountParam
        public volatile bool Joined;        // starter: the partner's game is in the battle too
        public volatile bool Solo;          // starter: the partner could not join
        public bool ParamDone;
        public bool Overridden;             // joiner: the starter's param has been put in place
        public bool CoreSeen;               // BattleHooks saw the battle start
        public bool Loaded;                 // the battle loaded its encounter here (Load* hooks)
        public int EncountId;               // joiner: the starter's encounter id (used if PARAM never came)
        public bool CoopFileWritten;
        public volatile bool PartnerInBattle;   // starter: the joiner's battle reached its first turn ("INBATTLE")
        public long StartedAt;
    }

    private class Join
    {
        public int Id; public uint Seed; public int Reason; public int EnemyId; public int EncountId;
        public long Since, LastTry;
        public volatile byte[]? Param;
    }

    private readonly CoopLog _log;
    private readonly Session _session;
    private readonly string _dir;
    private readonly IHook<RequestEncountFn> _requestEncount;
    private readonly IHook<SetEncountParamFn> _setEncountParam;
    private readonly IHook<EnemyForceEncountFn> _enemyForceEncount;
    private readonly IHook<LoadFn>[] _loads = new IHook<LoadFn>[5];
    private readonly delegate* unmanaged<nint> _getFieldManager;
    private readonly delegate* unmanaged<nint, nint> _holderObject;
    private readonly object _lock = new();
    private volatile Battle? _current;
    private volatile Join? _join;
    private int _counter;

    // Boss wait: our held Gatekeeper request (a copy of its FFldEnemyHolder) and the boss the partner waits at.
    private class BossHold { public int EnemyId; public int Reason; public nint Holder; public long Since; }
    private BossHold? _boss;
    private volatile int _partnerReadyAt = -1;
    private bool _releasing;
    private int _bossEnemyId = -1;                         // Gatekeeper asked for, battle not announced yet

    public Battle? Current => _current;

    public EncounterSync(IReloadedHooks hooks, CoopLog log, Session session, string runDir)
    {
        _log = log;
        _session = session;
        _dir = runDir;
        try { File.Delete(Path.Combine(runDir, "join_go.txt")); File.Delete(Path.Combine(runDir, "boss_solo.txt")); } catch { }
        var exe = (long)Process.GetCurrentProcess().MainModule!.BaseAddress;
        _getFieldManager = (delegate* unmanaged<nint>)(exe + RvaGetFieldManager);
        _holderObject = (delegate* unmanaged<nint, nint>)(exe + RvaHolderObject);
        _requestEncount = hooks.CreateHook<RequestEncountFn>(RequestEncountImpl, exe + RvaRequestEncount).Activate();
        _setEncountParam = hooks.CreateHook<SetEncountParamFn>(SetEncountParamImpl, exe + RvaSetEncountParam).Activate();
        _enemyForceEncount = hooks.CreateHook<EnemyForceEncountFn>(EnemyForceEncountImpl, exe + RvaEnemyForceEncount).Activate();
        _loads[0] = hooks.CreateHook<LoadFn>((c, p) => { OnParam(c, p, "LoadResidentData"); return _loads[0].OriginalFunction(c, p); }, exe + RvaLoads[0]).Activate();
        _loads[1] = hooks.CreateHook<LoadFn>((c, p) => { OnParam(c, p, "LoadData"); return _loads[1].OriginalFunction(c, p); }, exe + RvaLoads[1]).Activate();
        _loads[2] = hooks.CreateHook<LoadFn>((c, p) => { OnParam(c, p, "LoadEnemyData"); return _loads[2].OriginalFunction(c, p); }, exe + RvaLoads[2]).Activate();
        _loads[3] = hooks.CreateHook<LoadFn>((c, p) => { OnParam(c, p, "PreLoad"); return _loads[3].OriginalFunction(c, p); }, exe + RvaPreLoad).Activate();
        _loads[4] = hooks.CreateHook<LoadFn>((c, p) => { OnParam(c, p, "Encount"); return _loads[4].OriginalFunction(c, p); }, exe + RvaEncount).Activate();
        log.Info("Encounter sync on (RequestEncount, battle PreLoad/Encount/Load*, EnemyManager::ForceEncount)");
    }

    // ---------------------------------------------------------------- starter side (game thread)

    private void RequestEncountImpl(nint mgr, nint holder, int reason)
    {
        try
        {
            var c = *(nint*)(holder + 8);
            if (c != 0) _log.Quiet($"RequestEncount reason {reason}: Shadow id {*(int*)(c + CompId)} type {*(byte*)(c + CompType)}");
        }
        catch { }
        // The partner's battle is about to start here: a Shadow touched meanwhile would start a second one.
        if (_join != null) { _log.Info($"local encounter (reason {reason}) ignored: joining the partner's battle"); return; }
        // (Boss wait is done in the field script now: a Gatekeeper can't be touched until both players are near
        // it. HoldBoss stays for reference; holding the request itself skipped Fuuka's prompt script.)

        var before0 = *(nint*)(mgr + MgrRequest);
        _requestEncount.OriginalFunction(mgr, holder, reason);
        try
        {
            bool stored = *(nint*)(mgr + MgrRequest) == *(nint*)holder && *(nint*)(mgr + MgrRequest + 8) == *(nint*)(holder + 8);
            if (!stored || _current != null) return;                 // refused, or this battle is already ours
            if (before0 == *(nint*)holder && !_session.Connected) return;   // same contact asked again next frame

            var comp = *(nint*)(holder + 8);
            int enemyId = comp != 0 ? *(int*)(comp + CompId) : -1;
            int encount = comp != 0 ? *(int*)(comp + CompEncount) : 0;
            int type = comp != 0 ? *(byte*)(comp + CompType) : -1;
            _log.Info($"encounter requested: reason {reason}, Shadow id {enemyId} type {type} encount {encount}");

            if (!_session.Connected) return;                     // playing alone: an ordinary battle
            if (type == Gatekeeper)
            {
                // A strike on a boss only opens Fuuka's "ready?" prompt; "not ready" cancels it. Announce the battle
                // when it really loads (PreLoad), not now.
                _bossEnemyId = enemyId;
                _log.Info($"boss Shadow {enemyId}: waiting for the prompt answer");
                return;
            }
            var b = new Battle { Id = NewId(), Seed = NewSeed(), Starter = true, StartedAt = Environment.TickCount64 };
            lock (_lock) _current = b;
            BattleRng.Arm(b.Seed);
            _session.Send($"BATTLE {b.Id} {b.Seed} {reason} {enemyId} {encount}");
        }
        catch (Exception e) { _log.Error("RequestEncount hook: " + e.Message); }
    }

    private void SetEncountParamImpl(nint core, nint param)
    {
        OnParam(core, param, "SetEncountParam");
        _setEncountParam.OriginalFunction(core, param);
    }

    // Every place the battle takes its FBtlEncountParam. Starter: send it once. Joiner: replace ours with the
    // starter's (every call: each load step gets the param passed in again).
    private void OnParam(nint core, nint param, string where)
    {
        try
        {
            var b = _current;
            // A battle starts here while the partner's battle is pending (both touched the boss): this battle becomes
            // the partner's - same id, seed and encounter - instead of a second, separate one.
            if (b == null && _join is { } pj)
            {
                b = new Battle { Id = pj.Id, Seed = pj.Seed, Starter = false, Param = pj.Param, EncountId = pj.EncountId, StartedAt = Environment.TickCount64 };
                lock (_lock) { _current = b; _join = null; }
                BattleRng.Arm(pj.Seed);
                _log.Info($"{where}: our own battle start adopted as the partner's battle {pj.Id}");
            }
            // A battle that did not come through RequestEncount (Fuuka's boss prompt, events): announce it here,
            // before anything is loaded or rolled, so the partner joins with exactly this encounter.
            if (b == null && _session.Connected && _join == null)
            {
                b = new Battle { Id = NewId(), Seed = NewSeed(), Starter = true, StartedAt = Environment.TickCount64 };
                lock (_lock) _current = b;
                BattleRng.Arm(b.Seed);
                _session.Send($"BATTLE {b.Id} {b.Seed} 5 {_bossEnemyId} {*(int*)param}");
                _bossEnemyId = -1;
                _log.Info($"battle {b.Id} started without an encounter request (encount {*(int*)param}): announced at {where}");
            }
            if (b != null)
            {
                b.Loaded = true;
                if (!b.CoopFileWritten)
                {
                    // For the Lua script: the partner's units go automatic before the first turn's menu opens.
                    b.CoopFileWritten = true;
                    var test = _session.TestSlots();
                    var slots = test.Count > 0 ? test : _session.RemoteSlots();
                    try { File.WriteAllText(Path.Combine(_dir, "battle_coop.txt"), $"{b.Id} {string.Join(",", slots)}"); } catch { }
                }
            }
            if (b != null && b.Starter && !b.ParamDone)
            {
                b.ParamDone = true;
                var bytes = new byte[ParamSize];
                Marshal.Copy(param, bytes, 0, ParamSize);
                b.Param = bytes;
                _session.Send($"PARAM {b.Id} {Convert.ToHexString(bytes)}");
            }
            else if (b != null && !b.Starter)
            {
                var own = *(int*)param;
                if (!b.ParamDone)
                {
                    // The starter sends PARAM when its battle sets up - normally before ours. Wait briefly if not.
                    var until = Environment.TickCount64 + 3000;
                    while (b.Param == null && Environment.TickCount64 < until) Thread.Sleep(10);
                    b.ParamDone = true;
                    if (b.Param == null) _log.Info($"battle {b.Id}: no PARAM from the partner, only the encounter id is set");
                    _session.Send($"JOINED {b.Id}");
                }
                // Once, at the first consumer (PreLoad): the later steps get the param from there, after the game
                // has filled in more of it (PreemptiveOriginal) - copying the starter's PreLoad bytes again would undo that.
                if (!b.Overridden)
                {
                    b.Overridden = true;
                    if (b.Param is { } p) Marshal.Copy(p, 0, param, ParamSize);
                    else if (b.EncountId > 0) *(int*)param = b.EncountId;
                    if (own != *(int*)param) _log.Info($"{where}: our encounter {own} replaced by the partner's");
                }
            }
            _log.Info($"{where}: encount {*(int*)param} preemptive {*(byte*)(param + 4)}/{*(byte*)(param + 5)} " +
                      $"stage {*(int*)(param + 0x14)}.{*(int*)(param + 0x18)}" + (b != null ? $" (battle {b.Id}{(b.Starter ? "" : ", joined")})" : ""));
        }
        catch (Exception e) { _log.Error($"{where} hook: " + e.Message); }
    }

    // Gatekeeper touched while co-op: hold the request until the partner's game has asked for this boss too.
    // Returns true = swallowed (the field asks again or simply waits; we re-issue it in Tick).
    private bool HoldBoss(nint holder, int reason)
    {
        var comp = *(nint*)(holder + 8);
        if (comp == 0 || *(byte*)(comp + CompType) != Gatekeeper || _current != null || !_session.Connected) return false;
        var id = *(int*)(comp + CompId);
        if (_boss == null || _boss.EnemyId != id)
        {
            var copy = (nint)NativeMemory.Alloc(16);
            Buffer.MemoryCopy((void*)holder, (void*)copy, 16, 16);
            if (_boss != null) NativeMemory.Free((void*)_boss.Holder);
            _boss = new BossHold { EnemyId = id, Reason = reason, Holder = copy, Since = Environment.TickCount64 };
            _session.Send($"READY {id}");
            _log.Info($"boss (Shadow id {id}, reason {reason}): waiting for the partner" +
                      (_partnerReadyAt == id ? " - partner is already here" : ""));
        }
        return true;
    }

    // Game thread: both players are at the boss -> the host starts it; the guest's held request joins the host's.
    private void TickBoss(nint mgr)
    {
        var bh = _boss;
        if (bh == null) return;
        if (_join is { } j && j.EnemyId == bh.EnemyId)
        {
            _boss = null;
            JoinWith(mgr, j, bh.Holder);
            NativeMemory.Free((void*)bh.Holder);
            return;
        }
        // F8 (Lua writes boss_solo.txt): don't wait any longer - this game starts the boss, the partner joins if it can.
        var solo = Path.Combine(_dir, "boss_solo.txt");
        bool alone = File.Exists(solo);
        if (alone) { try { File.Delete(solo); } catch { } }
        if (!alone && (_session.MyRole != Session.Role.Host || _partnerReadyAt != bh.EnemyId || _current != null)) return;
        _boss = null;
        _partnerReadyAt = -1;
        _log.Info($"boss {bh.EnemyId}: " + (alone ? "starting without waiting (F8)" : "both players ready, starting"));
        _releasing = true;
        try { RequestEncountImpl(mgr, bh.Holder, bh.Reason); }
        finally { _releasing = false; NativeMemory.Free((void*)bh.Holder); }
    }

    // Request the partner's battle here with this holder (a Shadow of our floor) and become part of it.
    private bool JoinWith(nint mgr, Join j, nint holder)
    {
        var comp = *(nint*)(holder + 8);
        var mine = *(int*)(comp + CompEncount);
        if (j.EncountId > 0) *(int*)(comp + CompEncount) = j.EncountId;
        _requestEncount.OriginalFunction(mgr, holder, j.Reason);
        bool stored = *(nint*)(mgr + MgrRequest) == *(nint*)holder && *(nint*)(mgr + MgrRequest + 8) == comp;
        _log.Info($"join {j.Id}: Shadow id {*(int*)(comp + CompId)} (wanted {j.EnemyId}), encount {mine} -> {j.EncountId}, " +
                  (stored ? "battle requested" : "request refused"));
        if (!stored) return false;
        var b = new Battle { Id = j.Id, Seed = j.Seed, Starter = false, Param = j.Param, EncountId = j.EncountId, StartedAt = Environment.TickCount64 };
        lock (_lock) { _current = b; _join = null; }
        BattleRng.Arm(j.Seed);
        return true;
    }

    // ---------------------------------------------------------------- partner side

    /// <summary>Control messages from the partner (watcher thread).</summary>
    public void OnMessage(string[] f)
    {
        int Id() => int.Parse(f[1]);
        switch (f[0])
        {
            case "BATTLE" when f.Length >= 6:
                lock (_lock)
                {
                    if (_current != null || _join != null)
                    {
                        _session.Send($"BUSY {Id()}");               // already in (or entering) another battle
                        return;
                    }
                    _join = new Join
                    {
                        Id = Id(), Seed = uint.Parse(f[2]), Reason = int.Parse(f[3]), EnemyId = int.Parse(f[4]),
                        EncountId = int.Parse(f[5]), Since = Environment.TickCount64
                    };
                }
                _log.Info($"partner started battle {f[1]} (encount {f[5]}): joining");
                break;
            case "PARAM" when f.Length >= 3:
            {
                var bytes = Convert.FromHexString(f[2]);
                if (_current is { } c && c.Id == Id()) c.Param = bytes;
                else if (_join is { } j && j.Id == Id()) j.Param = bytes;
                break;
            }
            case "JOINED" when f.Length >= 2:
                if (_current is { } jb && jb.Id == Id()) { jb.Joined = true; _log.Info($"battle {jb.Id}: partner joined"); }
                break;
            case "READY" when f.Length >= 2:
                _partnerReadyAt = Id();
                _log.Info($"partner is at the boss (Shadow id {f[1]})");
                break;
            case "INBATTLE" when f.Length >= 2:
                if (_current is { } ib && ib.Id == Id()) { ib.PartnerInBattle = true; _log.Info($"battle {ib.Id}: partner's battle is ready"); }
                break;
            case "BUSY" when f.Length >= 2:
                if (_current is { } sb && sb.Id == Id() && !sb.Joined) { sb.Solo = true; _log.Info($"battle {sb.Id}: partner busy -> solo"); }
                break;
        }
    }

    /// <summary>Once per frame on the game thread (UWorld::Tick hook).</summary>
    public void Tick()
    {
        var now = Environment.TickCount64;
        var b = _current;
        if (b != null)
        {
            if (b.Starter && !b.Joined && !b.Solo && now - b.StartedAt > 30000)
            { b.Solo = true; _log.Info($"battle {b.Id}: partner did not join in 30 s -> solo"); }
            if (!b.Loaded && !b.CoreSeen && now - b.StartedAt > 20000)
            { lock (_lock) _current = null; _log.Info($"battle {b.Id}: never started here, dropped"); }
        }

        var mgr0 = _getFieldManager();
        if (mgr0 != 0) TickBoss(mgr0);

        var j = _join;
        if (j == null) return;
        if (now - j.Since > 20000)
        {
            lock (_lock) _join = null;
            _session.Send($"BUSY {j.Id}");
            _log.Info($"could not join battle {j.Id} within 20 s");
            return;
        }
        if (now - j.LastTry < 1000) return;
        var mgr = _getFieldManager();
        if (mgr == 0) return;
        var state = *(int*)(mgr + MgrState);
        if (state != 2 && state != 3) return;                    // menu, event, loading...: try again later
        j.LastTry = now;
        try { File.WriteAllText(Path.Combine(_dir, "join_go.txt"), j.Id.ToString()); } catch { }
    }

    // Called by the Lua script (join_go.txt) on the game thread. While joining: request the battle with the
    // partner's Shadow (same spawn id) instead of the first usable one.
    private void EnemyForceEncountImpl(nint enemyMgr)
    {
        var j = _join;
        if (j == null)
        {
            // Dev test (no keyboard strike possible from outside): debug_reason.txt = "2" makes this call request a
            // battle with the first usable Shadow as a player strike, through our hook like the real strike code.
            var dbg = Path.Combine(_dir, "debug_reason.txt");
            if (File.Exists(dbg) && int.TryParse(File.ReadAllText(dbg).Trim(), out var reason))
            {
                File.Delete(dbg);
                var h = FirstUsable(enemyMgr, -1);
                _log.Info($"debug: strike request (reason {reason}) on {(h == 0 ? "nothing" : "Shadow id " + *(int*)(*(nint*)(h + 8) + CompId))}");
                if (h != 0) RequestEncountImpl(_getFieldManager(), h, reason);
                return;
            }
            _enemyForceEncount.OriginalFunction(enemyMgr);
            return;
        }
        try
        {
            var pick = FirstUsable(enemyMgr, j.EnemyId);
            if (pick == 0) { _log.Info($"join {j.Id}: no usable Shadow on this floor"); return; }

            JoinWith(_getFieldManager(), j, pick);
        }
        catch (Exception e) { _log.Error("ForceEncount hook: " + e.Message); }
    }

    // The Shadow with this spawn id, else the first usable one (ForceEncount's own test: resolved holder object's
    // vfunc +0x4B8 returns false).
    private nint FirstUsable(nint enemyMgr, int wantedId)
    {
        var data = *(nint*)(enemyMgr + EnemyArray);
        var num = *(int*)(enemyMgr + EnemyArray + 8);
        nint pick = 0;
        for (int i = 0; i < num && i < 256; i++)
        {
            var h = data + i * 16;
            var obj = _holderObject(h);
            if (obj == 0 || *(nint*)(h + 8) == 0) continue;
            var vt = *(nint**)obj;
            if (((delegate* unmanaged<nint, byte>)vt[0x4B8 / 8])(obj) != 0) continue;
            if (pick == 0) pick = h;
            if (*(int*)(*(nint*)(h + 8) + CompId) == wantedId) return h;
        }
        return pick;
    }

    /// <summary>The battle is over on this game (Lua battle_end.txt).</summary>
    public void OnBattleEnd()
    {
        lock (_lock) { if (_current is { } b && (b.CoreSeen || b.Loaded)) _current = null; }
        _partnerReadyAt = -1;
    }

    private int NewId() => ++_counter * 2 + (_session.MyRole == Session.Role.Guest ? 1 : 0);
    private static uint NewSeed() => (uint)Random.Shared.Next(1, int.MaxValue);
}
