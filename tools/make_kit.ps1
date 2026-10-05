# Builds dist\P3R-Coop-Kit.zip for the partner's PC: Reloaded-II with the P3R mod libraries and the co-op mod
# (copied from this PC's working install), the co-op Lua scripts as a normal UE4SS mod, the link app, and the
# installer (tools\kit). Rebuild it after every change to the mod, the Lua scripts or the link.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$src = Join-Path $env:USERPROFILE 'Reloaded-II'
$out = Join-Path $repo 'dist\P3R-Coop-Kit'
$zip = Join-Path $repo 'dist\P3R-Coop-Kit.zip'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$rl = Join-Path $out 'files\Reloaded-II'
New-Item -ItemType Directory -Force $rl | Out-Null

# 1. Reloaded-II program + mods, without this PC's settings, logs and research/dev mods.
$ue = 'Mods\p3rpc.UE4SS.Reloaded'
robocopy $src $rl /E /NFL /NDL /NJH /NJS /NP `
    /XD (Join-Path $src 'User') (Join-Path $src 'Apps') (Join-Path $src "$ue\CXXHeaderDump") `
        (Join-Path $src "$ue\Mods\CoopRecon") (Join-Path $src "$ue\Mods\P3RCoop") `
    /XF UE4SS.log *.log | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

# 2. Co-op Lua scripts as the UE4SS mod "P3RCoop" (the dev PC loads them from the repo through CoopRecon).
$scripts = Join-Path $rl "$ue\Mods\P3RCoop\Scripts"
New-Item -ItemType Directory -Force $scripts | Out-Null
Copy-Item (Join-Path $repo 'tools\ue4ss-lua\P3RCoop.lua'), (Join-Path $repo 'tools\ue4ss-lua\BattleRecon.lua') $scripts
Copy-Item (Join-Path $repo 'tools\kit\main.lua') $scripts
@'
CheatManagerEnablerMod : 0
ActorDumperMod : 0
ConsoleCommandsMod : 0
ConsoleEnablerMod : 0
SplitScreenMod : 0
LineTraceMod : 0
BPModLoaderMod : 1
BPML_GenericFunctions : 1
jsbLuaProfilerMod : 0
P3RCoop : 1
; Built-in keybinds, do not move up!
Keybinds : 0
'@ | Set-Content (Join-Path $rl "$ue\Mods\mods.txt") -Encoding ASCII

# UE4SS for players: no debug console window, no hot reload (it would load the scripts twice).
$ini = Join-Path $rl "$ue\UE4SS-settings.ini"
(Get-Content $ini) -replace '^GuiConsoleEnabled = 1', 'GuiConsoleEnabled = 0' `
                   -replace '^GuiConsoleVisible = 1', 'GuiConsoleVisible = 0' `
                   -replace '^EnableHotReloadSystem = 1', 'EnableHotReloadSystem = 0' | Set-Content $ini -Encoding ASCII

# 3. Mod settings: keep rendering when the window is in the background (a paused game stalls the partner).
$essentials = Join-Path $rl 'User\Mods\p3rpc.essentials'
New-Item -ItemType Directory -Force $essentials, (Join-Path $rl 'User\Misc') | Out-Null
'{ "RenderInBackground": true }' | Set-Content (Join-Path $essentials 'Config.json') -Encoding ASCII

# 4. Link app (framework-dependent; runs on the .NET 9 runtime that Reloaded-II needs anyway).
dotnet publish (Join-Path $repo 'src\P3RCoopLink') -c Release -o (Join-Path $out 'files\Link') --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# 5. Installer + readme.
Copy-Item (Join-Path $repo 'tools\kit\install.ps1'), (Join-Path $repo 'tools\kit\Install.bat'), (Join-Path $repo 'tools\kit\README.txt') $out

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
'{0}  ({1:N1} MB)' -f $zip, ((Get-Item $zip).Length / 1MB)
