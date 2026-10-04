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
