# P3R Co-op — MODLOG

Journal for the Persona 3 Reload multiplayer mod. Anything not written here is lost at context compaction.

## Goal (2026-10-04)
"Block Tales"-style co-op for Persona 3 Reload, played in Tartarus:
- Player 2 **joins** the host's game while in Tartarus (press a button on their controller → pick a character).
- That character joins the active party and is **P2's**: in battle, P2 alone picks that character's commands.
- P2 leaves → the character goes back to the host (or AI tactics).
- LAN is the comfortable target. The friend may not own the game.

Done = working in game + a 20-45 s clip of P2 taking their turn in a Tartarus battle.

## DESIGN v2 (2026-10-04, user confirmed "A") — supersedes the controller-routing design below
Real two-game co-op, Block Tales style. Both players own P3R and run it with the mod, on LAN.
- **Join:** guest connects to host over LAN. The guest's game receives a snapshot of the host's save (party,
  stats, Personas, Tartarus progress) and loads it into a scratch slot, so both games hold the same party.
  The guest's own saves are never touched.
- **Duos:** the host's party of 4 is split in two. Duo A = MC + 1 member → host. Duo B = the other 2 → guest.
  Each player walks their duo leader around Tartarus independently; their partner follows them.
- **Same floor:** both games must build the identical Tartarus floor (host is the authority: send seed or
  layout). Shadows on the floor are host-owned; the guest's copies mirror them.
- **Seeing each other:** in each game the other duo is shown by the existing partner actors, but driven by
  network position/animation instead of following the leader. On the guest's game the local player actor
  is reskinned as the Duo B leader.
- **Battles (Block Tales join):** a duo touches a shadow → battle on that player's game with their duo; the
  other player gets a "Join battle" prompt; if they join, both games run the battle with all 4 members and
  each player commands their own duo's turns.
- **Boss fights wait for both players (user request 2026-10-04):** a boss/gatekeeper battle (floor guardians,
  full-moon bosses, `AFldBossBattleScript`, `AFldDungeonLayoutDirectEnemyActor.GateKeeper`) never starts with
  one duo. Touching the boss (or its door/event) shows "Waiting for <player>…" and freezes the encounter
  until the other duo arrives nearby and confirms; then both games start the same battle with all 4 members.
  (Block Tales does the same: everyone must be near the boss.)
- **Stats come from the host's save (user, 2026-10-05):** party levels, Personas, gear, items are the host's;
  the guest's own save is never used for co-op.
- **State authority:** the host's game owns the save. The guest sends Duo B results (HP/SP/EXP/items picked
  up) back to the host after each battle or pickup.

### Order of work (riskiest first; each is a go/no-go spike)
- M0 tooling: Reloaded-II + libs + UE4SS live view/dump working on this build.
- M1 LAN link: host/guest connect; each game prints the other's floor and position. (Proves the transport.)
- M2 same floor: capture the Tartarus floor generator's seed/layout on host, replay it on guest. **Biggest
  unknown — if this fails, A needs a fallback (e.g. the guest's floor is streamed tile by tile).**
- M3 ghosts: drive partner actors from network positions; reskin the guest's player actor.
- M4 solo duo battles: each duo fights its own encounters on its own game; results synced to the host.
  Playable milestone.
- M5 joint battles: second duo joins a battle; command lockstep + RNG seed sync + host HP/SP correction
  after every action.

## Recon results (2026-10-04, game build Aug 2025)
Setup that works: Reloaded-II 1.31.0 (needs .NET 9 Desktop Runtime — installed by user) at
`C:\Users\mbasd\Reloaded-II`, app `p3r.exe`, mods installed by hand from GitHub releases into `Mods\<ModId>`:
reloaded.sharedlib.hooks 1.16.3, Reloaded.Memory.SigScan.ReloadedII 1.2.14, SharedScans.Reloaded 1.3.2,
reloaded.universal.fileemulationframework 2.3.0, UTOC.Stream.Emulator 2.0.3, UnrealEssentials 2.0.3,
p3rpc.nativetypes 1.8.1, p3rpc.essentials 1.3.0, p3rpc.UE4SS.Reloaded 3.0.2.
Launch: `Reloaded-II.exe --launch "D:\...\P3R.exe"`. Only one P3R at a time (a 2nd launch hangs in FEF init).
- **UE4SS gotcha:** engine version autodetect fails on this build → `[EngineVersionOverride] MajorVersion = 4,
  MinorVersion = 27` in `Mods\p3rpc.UE4SS.Reloaded\UE4SS-settings.ini`, else "PS scan timed out" fatal.
- Recon Lua mod: `Mods\p3rpc.UE4SS.Reloaded\Mods\CoopRecon` (F6 = class census → `P3R-Coop\recon\`).
- SDK dump (Ctrl+H, 3.6 s): copied to `P3R-Coop\research\sdk\` (game package = `xrd777.hpp`, 22k lines).
- Persistent world is `/Game/Xrd777/Maps/LV_Xrd777_P`; dungeon/battle are streamed sublevels
  (`LV_DungeonFloor_C`, `LV_DungeonControl_C`, `LV_BtlCentralLevel_C`).

### Tartarus (field)
- `AFldDungeonBase`: m_pFloorAutoGenerator (AFldDungeonAutoGenerateActor, no reflected fields), m_pEnemyManager
  (AFldEnemyManager: Enemies TArray<FFldEnemyHolder>, bBattle, ForceEncount()), m_pPartnerManager
  (AFldPartnerManager: Partners TArray<FFldPartnerHolder>), m_ControlComponent (list of UFldDungeon*Controller).
- Floors = grid of `AFldDungeonPartActor` {PartType, PartVariation, PartDirType} + `AFldDungeonLayout*Actor`
  spawn points (Start/Enemy/EnemyPath/TBox/Reaper/RetDev/SecretGate/Broken w/ Probability).
- Generation state machine (UFunctions on AFldDungeonGameInit): LOAD_START → INIT_FLOOR_LEVEL → CREATE_LAYOUT →
  CREATE_END → ADVANCE_* ; enemies: UFldDungeonEnemyController.AutoFloorLoad/AutoFloorCreateActor.
- `AFldDungeonControlActor.SetField(major, minor, idx)`, SetNextFieldFloor().
- `AFldDungeonGame.ForceEncount()`, `BattleSync()`, `IsBattle()`.
- Partners follow via `AFldFollowerController` (AAIController) → ghosts can be driven by moving these pawns.
- `UFldDungeonSeparatelyController` exists (split-up logic?) — no reflected fields; investigate later.
- Exe imports CRT `srand`/`rand` (+ BCryptGenRandom). Hypothesis: generator uses CRT rand → seed sync.

### Battle
- `ABtlManager.Control` → `ABtlControlBase` {BtlCore, Encount(FBtlEncountParam 0x24), PreLoad, SyncBattleFinish}.
- `UBtlCoreComponent`: CurrentAction (ABtlActor* whose turn), LatestAction, ActionList, PlayerList, EnemyList,
  EncountParameter, EncountIndex, BattleResult, delegates OnLogTurnStart/OnLogActStart.
- `ABtlActor` (APawn): AI (ABtlAI*: BtlManualAI = player-picked, BtlScriptAI = scripted), EnemyFlag, ToEndTurn().
- `UBtlActionManagerComponent`: ActionCommander/ActionType(EBtlCommandType)/ActionID/ActionTargets;
  **SetCommandWithTargets(commander, type, id, targets) + DoCommand()** → inject remote players' commands.
- `UBtlInputComponent`: InputKey, BtlInputSetCtrlEnable(bool), BtlInputKeyLock(...) → mute local input while
  the remote player is choosing.
- `UBtlOrder`: InterruptList, StandbyList (turn order).

### Floor-sync spike (M2) — 2026-10-04
- **RNG probe** (`src/p3rpc.coop/RngProbe.cs`): P3R.exe IAT slots for `api-ms-win-crt-utility!rand/srand`
  (P3R.exe+0x4044B88 / +0x4044B90) → hand-assembled stubs that log (return addr, tid[, seed]) into ring buffers.
  An inline asm hook on ucrtbase!rand crashed the game (prologue = `sub rsp,28h; call rel32`; relocation broke).
  FASM gotcha: `[gs:0x48]` assembled wrong → use `xor ecx,ecx; mov rcx,[gs:rcx+48h]`.
- UCRT rand state is **per thread**; worker threads hammer rand via P3R.exe+0xCA4E68 (~7000/s while loading),
  game-thread steady callers +0x1354156, +0x36451F5, +0x14329DE, +0x1163466. srand only at startup
  (P3R.exe+0x8A6A55 on the game thread, time seed; +0x3A80B78 on another thread).
- P3R.exe+0x13DB8xx (403-iteration loop, rand→float→sin/cos→TArray<float>) runs on floor load but is a visual
  scatter, NOT the layout.
- `tools/find_natives.py` maps UFunction names → exec thunks via the FNameNativePtrPair tables. Native bodies:
  CREATE_LAYOUT +0x11A65D0, CREATE_END +0x11A62C0, FloorInit +0x11ACDE0, (DungeonInit?) +0x11AADE0.
- **CREATE_LAYOUT is a poll loop**: for idx in m_ControlComponent[0..0x18) (TArray at +0x2E0, idx at +0x51C)
  call vtable+0x448 (or +0x450 when byte +0x520 set) until component byte +0x139 is set; then state(+0x518)=9.
  Measured: ~0 game-thread rand per tick. The layout RNG is elsewhere (other state, a component virtual, an
  own RNG, or a worker thread) — still unknown.
- Floor fingerprint (Lua, polling every 2 s over FldDungeonPartActor): first floor `8db2cf7d` = 7 parts on a
  3200-unit grid (types 1,3,4,4,6,6,11).
- AutoFloorCreateActor (UFunction) fires for stairs floors; teleport arrivals did not fire it.
- **RESULT (03:08): floors are deterministic per save + floor.** Session 1 (03:01) first teleport floor =
  `c549011b` (10 parts, auto floor via AutoFloorCreateActor); after a full game restart and loading the same
  save, the same floor = `c549011b` again. Also one floor revisited 3× in a session = `8c477e58` every time.
  → M2 is solved by giving the guest a copy of the host's save: both games build identical floors. Shadows /
  chests may still roll randomly → host-authoritative sync for those.
  (Caveat: no floor number in the logs yet — add it before relying on per-floor hashes.)
- **Command bridge** (CoopRecon Lua): write commands to `recon/cmd.txt`, results in `recon/bridge.log`
  (where | next | prev | setfield M m i | fp | census | eval <lua>). Needs the game to run unfocused →
  p3rpc.essentials `User\Mods\p3rpc.essentials\Config.json` = `{"RenderInBackground": true}`.
  **Gotcha:** essentials `IntroSkip` is an enum — writing `true` crashed the game twice (CLR fatal 0x80131506
  on live reload, startup JSON exception on launch).

### M1 link + M3 ghost — 2026-10-04 (working on one PC, user: "its nice")
- `src/P3RCoopLink` (net8 console, RollForward Major): `host` (UDP 27777 + LAN beacon on 27778), `join [ip]`
  (auto-finds a host by beacon), `mirror` (echo own state 0.25 s late, +250 X). Exchanges one line per 50 ms.
- IPC with the game = files in `P3R-Coop\run\`: `local.txt` (game writes `seq floor x y z yaw`),
  `remote.txt` (link writes peer line, write-then-rename), `game.log`, `link_status.txt`.
- `tools/ue4ss-lua/P3RCoop.lua` (auto-loaded via UE4SS mod `P3RCoop` whose main.lua dofile()s it; hot-reload
  with the bridge: `eval return dofile(".../P3RCoop.lua")`, generation counter retires old loops).
  Player pawn = visible `BP_FldPlayerDUNGEON_C`; ghost = last visible `BP_FldPartner_C`, follow components
  (FldFollowerComp, FldPartnerComp) tick-disabled, controller tick paused, driven with AddMovementInput toward
  the target (snap if > 600 units). Ghost only shown when floor ids match.
- UE4SS Lua gotcha: `K2_GetComponentsByClass` returns a TArray → iterate with `:ForEach(function(i, e) e:get() end)`,
  not ipairs.
- Exit crash (coreclr 0xc0000602 when closing the game) happens without our mod too (02:20, before p3rpc.coop
  existed) → community stack shutdown issue, harmless.
- Saves: `%APPDATA%\SEGA\P3R\Steam\<steamid>\SaveDataNNN.sav` (~830 KB, XOR-obfuscated GVAS). For the guest we
  copy the host's save into a free slot (never overwrite theirs).
- **Hardcoded `C:\Users\mbasd\...` paths** in the Lua + link app must become %USERPROFILE%-relative before
  the friend installs.

### Battle command relay — WORKING on one PC (2026-10-04 12:13)
- Recon (poll-only Lua `BattleRecon.lua`): every unit, player or enemy, has `BtlScriptAI`; a decision fills
  ABtlAI.ActionType/ActionID/ActionSkillID/ActionItemID/ActionTargets, then BtlActionManagerComponent gets
  commander/type/id/targets and executes. Types seen: 1 attack (id 0), 2 skill (id = skill), 3 item (id = item),
  5 guard, 7 TAKEOVER/Shift (target = ally). Encounter: BtlCore.EncountParameter (EncountID, Preemptive).
- **UE4SS 3.0.1 crashes** with Lua RegisterHook on BtlActor:StandbyAction/ToEndTurn and on
  BtlActionManagerComponent:SetCommand* while a skill is chosen (AV reading 0x4abd in the ProcessEvent hook
  path). Poll properties instead; do native hooks in C#.
- Lua `am:SetCommand(); am:DoCommand()` at turn start does NOT work: the menu/AI decision later overwrites it
  (and in one case the unit's turn was wasted — "Ken didn't do anything").
- Native (C# `BattleHooks.cs`): SetCommand impl P3R.exe+0x10407F0 (this, commander, byte type, int id) — the
  SetCommandWithTargets UFunction = SetCommand + targets; DoCommand impl +0x102FCE0 (this). Thunks via
  find_natives.py (+0x15E63E0 / +0x15E4A00; the other table hit is the Z_Construct link table).
- Slots: P<i> = BtlCore.PlayerList index (+0x2F8), E<i> = EnemyList (+0x308); actor→core at +0x3E0, AI +0x3D0.
- **Remote turn loop:** DoCommand for a slot in run/remote_slots.txt is held (hook returns without calling the
  original) → cmd_request.txt "b n slot" → command arrives in remote_cmds.txt "b n slot type id targets" →
  C# writes kick.txt → Lua (game thread) calls am:DoCommand() again → hook writes type/id/targets into the
  action manager + AI → original runs. Verified 4× in one battle (Ken chose Guard locally, ran the bot's
  Attack E0), battle finished normally. Local units' commands go to local_cmds.txt for the link to send.
- `P3RCoopLink bot` = stand-in partner (answers held turns with "attack E0" after 1.5 s).
- Testing alone: `tools/shot.ps1` (desktop grab of the "P3R" window; um's gfxcapture needs an ffmpeg download),
  `um win drive --proc P3R` for keys (Space confirm, C cancel/Guard, Enter talk/advance), bridge teleport of the
  player pawn (`K2_SetActorLocation`) next to a `BP_FldEmNormal_C` to start a fight. ForceEncount() does nothing
  on floor 214. Teleporter interaction box in the lobby = `BP_DHIT_BOX_C` at (1490,-470).
- Launch hang: P3R sometimes stalls in Reloaded mod loading (after "R-II SteamHook … hooked") when launched
  right after a previous instance crashed/closed; kill the stuck PID and launch again. Reloaded console is now
  off (`ShowConsole=false`) — a selected console freezes the loader too.

### Remote-turn crash FIXED (2026-10-04 14:55)
- Root cause: SetCommandTargets impl (+0x1040820) takes the TArray by value and FREES its data at the end
  (`jmp FMemory::Free` at +0x10408C6; MSVC callee-destroys by-value args). We passed CRT-heap memory → game
  allocator freed foreign memory (+ we freed it again) → SEHException "External component has thrown an
  exception", crash ~12 s later. Fix: header in our memory, data allocated by the game's
  TArray::ResizeForCopy(+0x950CF0, this, NewMax, PrevMax) → hand it over, never free it ourselves.
- Also fixed: watcher wrote kick.txt repeatedly → double DoCommand (Ken acted twice); now once per held turn,
  plus a 300 ms duplicate guard. Bot keyed requests by text only → stalled when a new game session reused
  "1 4 P3"; now keyed by text + file write time.
- Verified: full battle with 3 remote turns (n4, n7, n13), no crash, no duplicates. (Party later wiped because
  the test loop spams Attack — game over is normal game logic.)
- Title flow (keys): content warning (auto) → network prompt C → "network disabled" OK Space → intro Enter →
  press any button Enter → title "Load" Enter → slot 7 Space → confirm Space.

### Shared battle rolls WORK (2026-10-04 16:25) — `BattleRng.cs`
- rand IAT slot → stub: if armed && TEB thread id == game thread → managed `Next(ret, [rsp+30h])`; battle-range
  call sites (RVA 0xF80000-0x1080000, 0x11E0000-0x1200000) get a private MSVC-LCG stream per site seeded from
  one shared seed; RandRange helper (+0x11AC276) keyed by its caller. Lazy arm: Arm(seed) → first battle-site
  call resets all streams. Game thread taken from the FloorInit hook (Reloaded's init thread is NOT the game
  thread: 12144 vs 4448). Lua writes run/battle_end.txt at battle end → Disarm (logs per-site counts) → re-arm.
- Test (tools/run_battle.sh = launch, slot 7, Tartarus 214, first Normal Shadow, Space-Space each 5 s; compare
  with tools/compare_runs.py): runs A and B (seed 12345, fresh game each) on encounter 933 (Em0122+Em0129,
  preemptive 1) → **16 commands identical turn by turn** incl. enemy choices/targets; 18/20 sites same roll
  count (+0xFFBB62 435/439 and +0xFE5CFA 15/10 differ: frame-driven, isolated per site). Control run C
  (unarmed) diverged at command 3 (A/B: E1 gets a second action = crit/One More roll; C: Ken acts) and took 18
  commands. → per-site streams make battle outcomes reproducible across game instances.
- Test-driving gotchas: um win drive only types into the foreground window (tools/focus.ps1 before every key);
  the load dialog can appear late (press Space twice); FindAllOf order is not stable across launches (but the
  first Normal Shadow on 214 still gave encounter 933 every time); our SendInput resets the Windows idle timer.

### LAN command relay WORKS (2026-10-04 16:46)
- P3RCoopLink: TCP battle channel on 27777 next to UDP positions. Each side tails its game's local_cmds.txt →
  "C <line>" → peer appends to remote_cmds.txt; "H" heartbeat; auto-reconnect. Loopback test:
  tools/link_loopback_test.ps1 (host run\ + guest run_guest\ over 127.0.0.1). `fakeguest <hostRunDir>` plays the
  guest GAME (answers the host's held turn into run_guest\local_cmds.txt).
- In-game NET2 test: 5 remote Ken turns over TCP, no crash, battle won. NET1 crashed after the 3rd (coreclr AV
  +0x2e4ff2 again) — intermittent; suspected cause: the Lua kick calls DoCommand from inside an arbitrary UE4SS
  ProcessEvent callback (ExecuteInGameThread). `SetGamePaused(1)` does NOT freeze battle turns.
  `AFldDungeonGame::BattleSync` (+0x11A6100) is never called during battles → useless as a release point.
  Current build: Lua kick 0.2 s after the remote command arrives (BattleSync hook kept, harmless).
- **Next:** find a safe game-thread release point for held turns (the battle's own per-frame tick, e.g. a vtable
  Tick of BtlCoreComponent/BP_BtlControl_C, or hold at the AI-decision step instead of DoCommand); then joint
  battle start (same encounter on both games: same shadow by spawn index + FBtlEncountParam), joint battle ids
  instead of per-game counters, HP/SP sync at battle start, join window, boss wait, duo-only solo battles.
- Normal play is safe: remote_slots.txt empty + BattleRngTestSeed 0 → mod only logs.

### Held turns released at UWorld::Tick (2026-10-05) — commit 99447e5
- Release point = start of `UWorld::Tick` (+0x31EA3A0, `(world, tickType, dt)`): game thread, once per frame,
  before the battle updates. A held remote DoCommand is replayed there as soon as its command has arrived
  (Apply: SetCommand + SetCommandTargets, then the original DoCommand). Lua kick.txt stays only as a 3 s
  emergency fallback. Several battles: 0 kicks, 0 errors.
- Dead ends: `UGameEngine::Tick` (+0x30FE4A0) — suspected UE4SS conflict, don't hook. ABtlAI update
  (+0xFC6F60) runs for enemies only; +0x1274CB0 is the flowscript interpreter (hooking it slowed startup to
  ~80 s, two UE4SS startup crashes). Intermittent startup AVs (UE4SS 0x10 / P3R 0x108) seen, cause unknown.

### Boss test (2026-10-05, slot 4 = Adamah, Lv 76)
- Slot 4 (30.01) starts at the Adamah teleporter; stairs → 246F. Boss = placed unique Shadow
  `BP_FldEmUnique_194_C` at (0,-6400,888) (layout `DirectEnemy` marker, model 194). Touching it opens Fuuka's
  prompt "Враг силен… готов к бою?" → "Пошли." / "Я еще не готов." — **the boss-wait hook point** (hold the
  "Пошли." answer until the other player is near and confirms).
- Encounter 838: Em0444 + Em0445 minions; party PC01, PC04, PC02, PC03; remote slot P3.
  41 commands, 5 remote P3 turns held → released at world tick → executed; 0 errors, 0 duplicates, 0 kicks.
  Party wiped (test loop only attacks) → game over screen; normal game logic.
- Theurgia (T) opens the menu but is locked until the gauge fills; remote Theurgia (command type 11) IDs still
  uncaptured.

### Joint battle start (2026-10-05) — `Session.cs`, `EncounterSync.cs`, `PartySync.cs`
- **Design choice:** every battle is joint (the partner's game auto-joins), so both games keep the same state
  (EXP, HP, Shadows). If the partner can't join (menu for 20 s, own battle, no Shadow on its floor) the battle is
  "solo" and the starter commands all four.
- Field encounter request: `FieldManager::RequestEncount` +0x11DB770 (this, FFldEnemyHolder{Actor, EnemyComp}*,
  int reason) stores the request at +0x290 (reason +0x2A0) if none is pending; field manager = +0x115A590()
  (game instance +0x100, else global +0x566E958); field playable = state +0x280 in {2,3} (+0x11D9920).
  Reasons: 2..4 player strikes (ebx+2), 5 = forced/enemy contact.
- `AFldEnemyManager::ForceEncount` native +0x1130090: first Enemies[i] (+0x278) whose resolved object
  (+0x115A210) vfunc+0x4B8 is false -> RequestEncount(reason 5). `AFldDungeonGame::ForceEncount` = empty stub.
- UFldEnemyComp: type +0x118 (EFldEnemyType: 0 Normal, 1 Strong, 2 Rare, 3 Death, 4 Gatekeeper), id +0x11C,
  EncountID +0x120 = **0 for Tartarus Shadows** (encounter picked later).
- FBtlEncountParam reaches the battle through UBtlCoreComponent::LoadResidentData +0x1036B50 / LoadData
  +0x1035910 / LoadEnemyData +0x10368D0 (each copies it to core+0x298). SetEncountParam (+0x10408F0) is BP-only
  and NOT called for Tartarus battles.
- Protocol (net_out/net_in via link "M" lines): `BATTLE id seed reason enemyId encount`, `PARAM id hex(0x24)`,
  `JOINED id`, `BUSY id`, `HPSP id hp sp ...`. Battle ids: host even, guest odd; local battles negative.
  peer.txt "<role> <0|1>" heartbeat from the link (older than 3 s = alone).
- **Test A (host, `bot host`): WORKS** — encounter 933, BATTLE 2 -> JOINED, P2/P3 held 5x -> answered ->
  released, 19 commands, 0 errors, battle won.
- BtlCoreComponent may be reused between battles -> BattleHooks now resets its core at battle end.
- UGlobalWork getter +0x122AC90 (nativetypes sig): units +0x1B0 (0x2B4 each, Hp +0xC, Sp +0x10, persona equip
  +0x48), ActiveCharacters +0x1F6C. PartySync sends own duo HP/SP every second outside battles.
- `tools/dis.sh <va> [len]` = dumpbin disassembly (MSYS_NO_PATHCONV for its -flags).
- Field battle start (AFldDungeonGame ~+0x11A6047): param = encount controller (control comp 0x12) +0x1A8;
  battle control = +0xFFA4F0(); **PreLoad +0x1000C60** (copies param to ctl+0x298, sets +0x2BD) then
  **Encount +0x10062E0**. Replacing the param only in Load* froze a joined boss battle (assets of the original
  encounter preloaded) -> the joiner now replaces it ONCE, at PreLoad (later steps fill PreemptiveOriginal).
- **Test B (guest, `bot guest`, "start 838" without PARAM): join WORKS** (ForceEncount via Lua join_go.txt ->
  Shadow id 1 -> battle 1002 = encounter 838) but the boss froze on its skill 402: without the starter's PARAM
  the stage stays the joiner's floor stage (206.101 instead of the boss arena). Real starters always send PARAM.
- PARAM captured from a real host battle (encounter 933, floor 214):
  `A503000001000000FFFFFFFFFFFFFFFFFFFFFFFFCE00000065000000000000000000D2E5` (stage 206.101, preemptive 1).
- **Remote units automatic: WORKS** — Lua `BtlDataBaseComponent:SetManualOperate(actor, false)` at the joint
  battle's start (battle_coop.txt) makes the partner's units decide by AI with no menu, **MC included**; their
  DoCommand is held and replaced by the partner's command (guest test: P1 turn 3, P0 turn 4, both answered).
- **Boss wait:** Adamah 246 boss = `BP_FldEmUnique_194_C`, EnemyType 4 (Gatekeeper), id 0, the floor's only
  enemy. Forced request (ForceEncount) -> held, "READY 0" sent, field stays playable; partner BATTLE with
  enemyId 0 -> joined with the held request. But a forced request skips Fuuka's prompt script and gives encounter
  1 (one Em0027), not 838 -> the real prompt flow must be tested (keyboard Ctrl/click attack didn't register in
  this session; the 01:12 session triggered the prompt with W/S/Ctrl on top of the boss).
- Joining needs a usable Shadow on the joiner's floor (transit/boss floors have none or only the boss).
- Game over: MC dying in an attack-spam test ends the session (slot 7 vs encounter 933). Use slot 4 (Lv 76).
- Load menu cursor = newest save (slot 7); SLOT_UP presses can land on the title menu if the menu is late
  (pressed "New Game" once). Slot 6 = 263F top floor (no Shadows), slot 4 = 246 boss floor, slot 7 = 214.

### Theurgia, HP/SP, kit (2026-10-05 evening)
- **Theurgia** = AI decision type 11 + skill id, but the action manager gets SetCommand(type 2, same id)
  (Akihiko 282 "Копье громовержца" on all enemies, MC 260 on all allies). BattleHooks now relays type 11
  (from AI +0xAF0) and applies it as SetCommand(2, id) + AI type 11. **Remote MC Theurgia via bot: WORKS**
  (battle 3 turn 8: DECIDE type=11 id=260, ACTMGR type=2 id=260, battle won). Gauge fill for tests:
  `core.PlayerList[i]:AddTheurgiaGauge(7)` x40. Key T opens Theurgia, Space twice (skill, targets).
- **UGlobalWork layout on this build = nativetypes' Astrea one:** 13 units at +0x1B0, ActiveCharacters at
  **+0x24D4** (+0x1F6C read zeros). **HP/SP sync: WORKS** — an injected "HPSP 1 100 50 ..." showed up as MC
  100/50 in the next battle (db:GetUnitHP). The field HUD keeps showing MAX until it refreshes (cosmetic).
- **Save transfer (loopback): WORKS** — host `--save 4` -> guest got an identical SaveData002.sav (first free
  slot). Test override: env P3RCOOP_SAVEDIR.
- **Partner kit:** `tools/make_kit.ps1` -> dist/P3R-Coop-Kit.zip (34 MB): Reloaded-II + libs + co-op mod, UE4SS
  mod "P3RCoop" (scripts copied, CoopRecon left out, cheats/keybinds off, no GUI console/hot reload),
  RenderInBackground, link publish, install.ps1/Install.bat/README.txt (EN+RU). Installer: checks .NET 9
  desktop, finds P3R via Steam libraryfolders.vdf, installs to %USERPROFILE%\P3R-Coop, writes AppConfig +
  %APPDATA%\Reloaded-Mod-Loader-II\ReloadedII.json (backs up an existing one; LoaderSetupTimeout 120 s),
  "Host co-op"/"Join co-op" .bat (OEM code page) + desktop shortcuts. Dry run with P3RCOOP_KIT_TEST=1 and a
  fake USERPROFILE/APPDATA: OK. Dev PC host launcher: `Host co-op.bat` in the repo root.
- Paths are now %USERPROFILE%\P3R-Coop\{run,recon} everywhere (Lua, mod config default, link).
- F8 (Lua keybind) -> boss_solo.txt -> a held boss starts without waiting.
- (superseded below) **Still open:** real Fuuka-prompt boss flow under the hold (couldn't trigger the prompt by keys this
  session); MC Persona changes are not synced (guest's MC keeps its Persona -> damage can differ -> DESYNC ->
  solo fallback); the guest's own pawn is still the MC model; first real two-PC test.

### Status at pause (2026-10-04 ~12:50)
- **Crash on remote turns** (coreclr AV at +0x2e4ff2, 3x): first fixed a double kick (watcher rewrote kick.txt
  before the turn cleared → Ken acted twice). Still crashed on the first remote turn → suspect: in-place TArray
  rewrite left 0 targets when the local pick had none. New build (installed, UNTESTED) applies remote commands
  via the game itself: SetCommand (original) + SetCommandTargets impl P3R.exe+0x1040820 with a temp TArray copy.
- **Battle RNG:** battle logic uses CRT rand on the game thread (call sites P3R.exe+0xFD0000..0x1040000), mixed
  with frame-rate-dependent visual consumers (+0x36451F5, +0x1354156, ~33k calls/battle) → must give battle call
  sites their own seeded per-site streams (IAT rand stub routes return addresses in the battle range to a
  deterministic generator seeded per battle). Floors use the xorshift helper +0x11AC2B0 (dungeon code
  +0x1198000..0x119D600, one state object) — that's why floors repeat.
- Tools for solo testing: tools/launch.ps1 (single instance, retries stuck loads), tools/to_battle.sh (title →
  slot 7 → Tartarus 214 → battle), tools/shot.ps1 (screenshot + focus).
- Next: verify the SetCommandTargets fix; battle RNG streams; link app relays local_cmds/remote_cmds over LAN;
  encounter start sync + join window + boss wait.

## (old) Design v1: host-authoritative, controller-routed
Running two separate copies of P3R in sync (Tartarus floor gen, battle RNG, events) is out of scope.
Instead **one game runs on the host**, and the mod decides **which controller is allowed to drive the game**
at each moment:
- Exploration / host's characters' turns → P1 controller (+ keyboard).
- P2's character's turn → only P2's controller; P1 + keyboard are muted.

How P2's controller reaches the host is pluggable (the mod only sees "XInput controller #2"):
1. Couch: second pad on the host PC.
2. LAN / internet: Parsec or Sunshine+Moonlight; the guest's pad shows up as a virtual XInput pad on the host,
   the guest sees the stream. The guest does NOT need to own P3R.
3. (Later, optional) our own tiny LAN client that sends pad state over UDP straight into the mod.

## Install facts
- Steam app 2161700, `D:\SteamLibrary\steamapps\common\P3R`
- Exe: `P3R\Binaries\Win64\P3R.exe` (366 MB, packed — no UE version string visible). UE 4.27, IoStore paks
  (pakchunk0-5 + global.utoc). No anti-cheat.
- Imports: `XINPUT1_3.dll` (UE4 XInputInterface polls pads 0-3), `steam_api64.dll`, RawInput (kb/mouse).
- Saves: `%APPDATA%\SEGA\P3R\Steam\<steamid>\` ; config: `%LOCALAPPDATA%\P3R\Saved\Config\WinGDK\` (note: WinGDK,
  not WindowsNoEditor); logs: `%LOCALAPPDATA%\P3R\Saved\Logs\` (empty).
- Dev tools on PC: .NET SDK 7/8/10, Python 3.8. Bash sandbox has no network; use PowerShell for downloads.

## Route
Reloaded-II (1.31.0) + P3R community libs:
- `p3rpc.essentials` (base support; has NoPauseOnFocusLoss — needed so the host window keeps running while
  streaming / unfocused), Unreal Essentials (pak loading).
- `p3rpc.nativetypes` struct defs (UGlobalWork.ActiveCharacters = party; GetUnit(i) = FDatUnitWork).
- `p3rpc.UE4SS.Reloaded` (UE4SS 3.0.x fork, 2024) for the live object viewer / dump — recon only.
- **Our mod: `p3rpc.coop`**, a C# Reloaded-II code mod (net7.0-windows like essentials): hooks
  `XInputGetState` to route pads, sig-scan hooks into battle code to know whose turn it is, .NET sockets
  for the optional LAN client.
Reason: C# Reloaded mods are the community standard for P3R native code; .NET gives easy networking;
input routing at the XInput layer needs zero game reverse-engineering.

## Reference repos (cloned, read-only)
`C:\Users\mbasd\P3R-Coop\research\` — p3rpc.nativetypes, p3rpc.essentials, P3R-Mod-Menu.
Hook pattern: `IStartupScanner.AddMainModuleScan(sig)` → `IReloadedHooks.CreateHook<T>(fn, addr).Activate()`.

## Unknowns to resolve in game (next)
1. Does P3R accept input from controller #2 at all, or only #0? (decides remap vs. mute strategy)
2. Battle: where is "current acting unit" (char id of whose command menu is open)? → UE4SS live view on
   battle UObjects (ABtl*/UBtl*), or hook the command-menu open function.
3. Party: can we safely force P2's pick into UGlobalWork.ActiveCharacters mid-Tartarus?

## Restore path
- Saves: `C:\Users\mbasd\.universal-modder\backups\p3r-saves\20261004-015136.zip` (16 files, 11.9 MB) of
  `%APPDATA%\SEGA\P3R\Steam` → `um backup restore p3r-saves`
- Config: `...\backups\p3r-config\20261004-015137.zip` of `%LOCALAPPDATA%\P3R\Saved\Config`
- Files that appeared in the game folder `P3R\Binaries\Win64\` (delete to clean up): `steam_appid.txt`
  (Reloaded SteamHook, needed for --launch), `imgui.ini` (UE4SS GUI). The Ctrl+J object dump also lands
  there (`UE4SS_ObjectDump.txt`, 36 MB) — moved to `research\`. Otherwise uninstall = delete Reloaded-II folder.

## Log
- 2026-10-04: recon done, design chosen, waiting on OK to install Reloaded-II + back up saves.

### Boss wait v2, MC Persona, guest look (2026-10-05 night)
- **Guest look: WORKS.** Pawns are built from `AppCharacterComp` (ABP_AppCharacter_C +0x930): CategoryID 1,
  IndexID = character id (player 1 = MC, costume -1; partners e.g. 4/3/2, costume 0). `SetIndexID(id)`,
  `SetCostumeID(c)`, `SetCostume()` re-dress a pawn live (walk anims fine). P3RCoop.lua (guest role): our pawn
  takes the look of party slot 2 (party.txt from PartySync = ActiveCharacters), that partner pawn becomes MC
  and is the host's ghost; host role: the guest leader's pawn is the ghost. Restored when the role goes away.
  `K2_GetComponentsByClass` returns a Lua table in this UE4SS (ghost take_over fixed).
- **MC Persona: WORKS (bot tests).** Battle actor +0x328 = its FDatUnitWork (global data). UBtlDataBaseComponent::
  SetPersonaStockEquip (+0x1040B40) = SetEquip(+0x12044E0)(charId, index): unit = +0x122B3A0(gw, id), stock slot
  i valid if byte unit+0x4C+i*0x30 bit 0, equip = ushort unit+0x48. MC's relayed commands carry ` p<index>`
  (host sent "4 7 P0 1 0 E0 p1"); the receiving game calls SetEquip first ("P0 Persona 0 -> 1"). Outside
  battles the host sends MC's whole Persona block (0x244 bytes at unit+0x48) as `MCPERSONA <hex>`.
- **Boss (Adamah 246):** strike code (+0x11259A9) = RequestEncount(reason 2/3/4) with no boss check; a strike-
  or forced request on the Gatekeeper starts encounter 1 directly (no prompt) -> Fuuka's prompt + encounter 838
  are started by the boss/event side (UFldEnemyUniqueComp state 4, BP_DEVT_RefActor_C on the boss, an
  FldHitActorBOX at (0,-5000)). Keyboard Ctrl/LMB never reached the game's attack this session (behavior stayed
  1); forcing FldPlayerDungeonComp.NextBehavior = 7 crashed the game.
- **Boss wait v2: WORKS (gate tested, prompt not).** P3RCoop.lua: while co-op is on, a Gatekeeper and the model
  actors on it (BP_Em0093_003_C, BP_Em0062_003_C) get collision OFF until the partner's network position is within
  1500 of it (same floor, fresh <2 s); F8 = open for 2 min. EncounterSync: Gatekeeper requests are not announced
  (prompt may say "not ready"); any battle reaching PreLoad unannounced is announced there (BATTLE id seed 5
  bossId encount + PARAM) -> verified: partner joined. The request hold (HoldBoss/READY) is no longer used.
- **Crash pattern:** coreclr.dll+0x2E4FF2 with BattleHooks.WorldTickImpl on the stack = a native fault anywhere
  inside the world tick (our hook wraps it) - here from the forced NextBehavior and from a UE4SS Lua property read
  on FldHitActorBOX. Not a bug in the hook itself; avoid poking those objects.
- To verify by hand in real play: strike the boss with the partner far away (nothing should happen), then with
  the partner next to it (prompt -> "Пошли" -> both games in the boss fight).

### First real 2-PC session (2026-10-05 21:46, friend = host, early save, 3-member party) + fixes
- Host started battle 2 (encount 94, stage 203.101), guest joined with the same PARAM and seed, host's P1 command
  (skill 91) arrived and ran on the guest. Then the **host crashed**: battle_coop "P2,P3" but the party had only
  P0-P2 -> Lua `db:SetManualOperate(core.PlayerList[4] = nothing)` -> native AV. The guest's 21:51:15 c0000602
  is the normal game-exit failfast (game closed).
- **Lag sources:** remote units were set automatic only after the first DoCommand (so the guest had to press
  through a menu for the host's Yukari, 13 s); the joiner's battle starts ~7 s after the starter's, and that offset
  is added to every remote turn; the ghost pawn walks slower than a running player.
- **Fixes:** Lua skips slots beyond #PlayerList and waits until the party exists; battle_coop.txt is written at
  PreLoad (EncounterSync) so units go automatic before the first menu; joiner Lua -> battle_ready.txt -> C# sends
  `INBATTLE id`; the starter holds its first non-remote action until INBATTLE (30 s max, or solo/link lost);
  MCPERSONA applied only when different; ghost pulled along (20%/tick) when > 200 behind, snap > 1500.
- **Floor layout seed (user saw "floors out of sync", maybe not - the host hit a Shadow at once):** generator
  +0x119B140(this, params) -> setup +0x119EE60 memsets a 0x4938 ctx, copies params words +0 (ctx+0x490E),
  +2 (ctx+0x1A1C), +8 (ctx+0x1A1A), then XorSeed(ctx+0x1A40, params+2) (+0x11AC370: s0 = seed^0x0AED1A0C, ror 8,
  ^0xAA5A02FE, ror 8, ^0x11BE81C7, ror 8). XorProbe had shown the state on the stack (0xF7BC60).
  `FloorSync.cs`: host sends `FLOORSEED n` every 5 s; while connected both games replace params+2 with
  hash(n, word+0, word+8). Logs "floor layout: params [...]". **Untested in game**; Config `SyncFloors` (default
  true) turns it off.
