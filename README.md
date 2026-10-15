# P3R Co-op

Two-player co-op for **Persona 3 Reload** (Steam, PC), in the style of Block Tales. Each player runs their own copy of
the game; a small link app connects the two games over LAN or a virtual LAN (Radmin VPN, ZeroTier).

- The host's party is split into two duos: the **host** plays the protagonist + the 1st party member, the **guest**
  plays the 2nd + 3rd members.
- You see each other walking around Tartarus, on the same floor layout.
- Every battle is shared. When one player touches a Shadow, the other game joins the same battle, and each player
  gives the commands for their own duo. The partner's characters act by themselves on your screen with the
  partner's real choice.
- Levels, Personas and items come from the **host's save**; the guest gets a copy of it when connecting.

> Unofficial fan mod, work in progress. Not affiliated with ATLUS or SEGA. You need your own copy of the game on
> both PCs. LAN/VPN only, no online services are touched.

## Status (v0.1.0)

Tested in real two-PC sessions:

| Feature | State |
| --- | --- |
| Joint battles (either player starts, the other joins) | works |
| Commands relayed in lockstep, partner units on auto | works |
| Theurgia | works |
| Same Tartarus floor layouts on both PCs | works |
| Partner's character visible and moving on your floor | works |
| HP/SP kept equal outside battles | works |
| Items and money picked up in the field go to both players | works |
| Host's save copied to the guest | works |
| Shuffle Time: the guest's cursor follows the host's card | works |

Built and checked on one PC, not yet confirmed in a two-player session:

| Feature | State |
| --- | --- |
| Opened chest / broken pot disappears for the partner too | one-PC test passed |
| Heals from the camp menu (spells, items) synced | built, untested |
| The protagonist's Persona switch in battle shown in the partner's game | built, untested |
| Guest's field character keeps the guest leader's look after battles | one-PC test passed |
| All-Out Attack: the first answer (Go / Cancel) applies to both games | one-PC test passed (partner's Go) |
| Shadows on the map follow the host's positions | one-PC test passed |
| Floor boss waits until both players are near it (F8 = don't wait) | untested |
| Mouse is not locked to the game window while it is unfocused | untested |

Known issues: a report of the battle camera pointing at the wrong thing during the partner's turn (not reproduced
yet; battle.log now records camera targets). Items used or sold are not mirrored, only gains.

## Install

Download `P3R-Coop-Kit.zip` from the [Releases](../../releases) page on **both PCs**.

1. Unzip it anywhere and run `Install.bat`.
   - Needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0); the installer opens
     the download page if it is missing.
   - It finds P3R through Steam (or asks for `P3R.exe`) and installs Reloaded-II, the mod libraries, the co-op mod
     and the link app into `%USERPROFILE%\P3R-Coop`. The game folder is not modified; an existing Reloaded-II
     setup is backed up.
2. Both PCs must be on the same network (LAN / Wi-Fi, or Radmin VPN / ZeroTier).
3. Allow `P3RCoopLink` in Windows Firewall (private networks). It uses **TCP + UDP 27777** and UDP broadcast
   **27778** to find the host.

## Play

- **Host:** desktop shortcut *P3R Host co-op*, then type the save slot to share (its number in the Load menu).
- **Guest:** desktop shortcut *P3R Join co-op*, press Enter to find the host (or type the host's IP). When the link
  window says `HOST'S SAVE IS IN YOUR SLOT N`, load that slot.
- Both go to Tartarus. Keep the black link window open while playing.
- **F8** at a floor boss: fight it now instead of waiting for the partner.

Logs for bug reports: `%USERPROFILE%\P3R-Coop\recon\coop.log` and `battle.log`.

## How it works

- **`src/p3rpc.coop`** - Reloaded-II C# mod with native hooks into P3R.exe (Steam build, Aug 2025):
  - `BattleHooks` - every battle command goes through `UBtlActionManagerComponent::SetCommand/DoCommand`. A remote
    unit's `DoCommand` is held until the partner's command arrives, then applied and released at `UWorld::Tick`.
  - `BattleRng` - per-call-site random streams seeded per battle, so both games roll the same results.
  - `EncounterSync` - joint battle start (`RequestEncount`, `ForceEncount`, encounter parameters copied at load).
  - `FloorSync` - shared seed for the Tartarus layout generator.
  - `PartySync`, `ItemSync` - HP/SP, protagonist's Personas, item and money gains.
  - `AlloutSync` - the All-Out Attack prompt (`StartHoldup` / `DoGeneralAttack` / `CancelHoldup`).
  - `MouseRelease` - no cursor centering/clipping while the game is in the background.
- **`tools/ue4ss-lua`** - UE4SS Lua scripts: partner pawn and look swap, Shadows, chests, boss wait
  (`P3RCoop.lua`); auto-control of partner units, Shuffle Time, All-Out press, battle log (`BattleRecon.lua`).
- **`src/P3RCoopLink`** - the link app: TCP for messages and commands, UDP for positions, save transfer, LAN
  discovery. Also has test partners (`bot`, `mirror`, `fakeguest`) for one-PC testing.
- **`tools/kit`**, `tools/make_kit.ps1` - the partner kit and its installer.
- **`MODLOG.md`** - the development journal: offsets, findings, test results, crash notes.

## Building from source

Requires the .NET 8 SDK (Windows) and a working Reloaded-II + P3R setup.

```bash
dotnet build src/p3rpc.coop -c Release      # output goes to %USERPROFILE%\Reloaded-II\Mods\p3rpc.coop (see the .csproj)
dotnet build src/P3RCoopLink -c Release
powershell -ExecutionPolicy Bypass -File tools/make_kit.ps1   # -> dist/P3R-Coop-Kit.zip
```

`make_kit.ps1` copies Reloaded-II and the mod libraries from this PC's install, so build and test the mod first.

## Credits

The kit bundles these third-party projects, unchanged:
[Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II), Reloaded.Hooks, Reloaded.Memory.SigScan and
File Emulation Framework (Sewer56); [Persona 3 Reload Essentials](https://github.com/AnimatedSwine37/p3rpc.essentials)
and Unreal Essentials (AnimatedSwine37); P3RE Native Types and IO Store Emulator (Rirurin); Shared Scans (RyoTune);
p3rpc.UE4SS.Reloaded (a [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) port for Reloaded-II).
