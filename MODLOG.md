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
- **Command bridge** (CoopRecon Lua): write commands to `recon/cmd.txt`, results in `recon/bridge.log`
  (where | next | prev | setfield M m i | fp | census | eval <lua>). Needs the game to run unfocused →
  p3rpc.essentials `User\Mods\p3rpc.essentials\Config.json` = `{"RenderInBackground": true}`.
  **Gotcha:** essentials `IntroSkip` is an enum — writing `true` crashed the game twice (CLR fatal 0x80131506
  on live reload, startup JSON exception on launch).

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
