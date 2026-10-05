# P3R Co-op: setup on the partner's PC (started by Install.bat).
# Installs Reloaded-II + the P3R mod libraries + the co-op mod into %USERPROFILE%\P3R-Coop and makes the
# "Host co-op" / "Join co-op" launchers. The game folder itself is not changed.
$ErrorActionPreference = 'Stop'
$kit = $PSScriptRoot
$dest = Join-Path $env:USERPROFILE 'P3R-Coop'
$rl = Join-Path $dest 'Reloaded-II'

function Done($code) { if (-not $env:P3RCOOP_KIT_TEST) { Read-Host 'Press Enter to close' }; exit $code }

Write-Host '== P3R Co-op setup ==' -ForegroundColor Cyan

# 1. Reloaded-II needs the .NET 9 Desktop Runtime.
$runtimes = ''
try { $runtimes = (& dotnet --list-runtimes) -join "`n" } catch { }
if ($runtimes -notmatch 'Microsoft\.WindowsDesktop\.App 9\.') {
    Write-Host '.NET 9 Desktop Runtime is missing. Its download page opens now:' -ForegroundColor Yellow
    Write-Host 'install ".NET Desktop Runtime 9 - Windows x64", then run Install.bat again.'
    Start-Process 'https://dotnet.microsoft.com/download/dotnet/9.0'
    Done 1
}

# 2. Find P3R.exe in the Steam libraries (or ask).
function Find-P3R {
    $libs = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $p = Get-ItemProperty $key -ErrorAction SilentlyContinue
        if ($p.SteamPath) { $libs += $p.SteamPath }
        if ($p.InstallPath) { $libs += $p.InstallPath }
    }
    foreach ($steam in @($libs)) {
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in (Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches).Matches) {
                $libs += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    foreach ($l in ($libs | Select-Object -Unique)) {
        $exe = Join-Path $l 'steamapps\common\P3R\P3R\Binaries\Win64\P3R.exe'
        if (Test-Path $exe) { return (Resolve-Path $exe).Path }
    }
    return $null
}
$exe = Find-P3R
if (-not $exe) {
    Write-Host 'Persona 3 Reload was not found in Steam. Please pick P3R.exe (...\P3R\P3R\Binaries\Win64\P3R.exe).'
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = 'P3R.exe  (...\P3R\P3R\Binaries\Win64\P3R.exe)'
    $dlg.Filter = 'P3R.exe|P3R.exe'
    if ($dlg.ShowDialog() -ne 'OK') { Done 1 }
    $exe = $dlg.FileName
}
Write-Host "Game: $exe"

# 3. Files.
New-Item -ItemType Directory -Force $dest, (Join-Path $dest 'run'), (Join-Path $dest 'recon') | Out-Null
robocopy (Join-Path $kit 'files\Reloaded-II') $rl /E /NFL /NDL /NJH /NJS /NP | Out-Null
robocopy (Join-Path $kit 'files\Link') (Join-Path $dest 'Link') /E /NFL /NDL /NJH /NJS /NP | Out-Null
if (-not (Test-Path (Join-Path $rl 'Reloaded-II.exe'))) { Write-Host 'Copying failed.' -ForegroundColor Red; Done 1 }

# 4. Reloaded-II: P3R entry with the co-op mod set.
$mods = @('reloaded.sharedlib.hooks', 'Reloaded.Memory.SigScan.ReloadedII', 'SharedScans.Reloaded',
          'reloaded.universal.fileemulationframework', 'UTOC.Stream.Emulator', 'UnrealEssentials',
          'p3rpc.nativetypes', 'p3rpc.essentials', 'p3rpc.UE4SS.Reloaded', 'p3rpc.coop')
$app = Join-Path $rl 'Apps\p3r.exe'
New-Item -ItemType Directory -Force $app | Out-Null
[ordered]@{
    AppId = 'p3r.exe'; AppName = 'Persona 3 Reload'; AppLocation = $exe; AppArguments = $null; AppIcon = 'Icon.png'
    AutoInject = $false; EnabledMods = $mods; WorkingDirectory = (Split-Path $exe); SortedMods = $mods
    PreserveDisabledModOrder = $true; DontInject = $false; IsMsStore = $false
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $app 'AppConfig.json') -Encoding UTF8

# 5. Reloaded-II settings point at this install (an existing Reloaded-II setup is backed up first).
$rd = Join-Path $env:APPDATA 'Reloaded-Mod-Loader-II'
New-Item -ItemType Directory -Force $rd | Out-Null
$rj = Join-Path $rd 'ReloadedII.json'
if (Test-Path $rj) {
    $bak = "$rj.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Copy-Item $rj $bak
    Write-Host "Your old Reloaded-II settings were saved as $bak"
}
[ordered]@{
    LoaderPath32 = "$rl\Loader\x86\Reloaded.Mod.Loader.dll"; LoaderPath64 = "$rl\Loader\x64\Reloaded.Mod.Loader.dll"
    LauncherPath = "$rl\Reloaded-II.exe"
    Bootstrapper32Path = "$rl\Loader\x86\Bootstrapper\Reloaded.Mod.Loader.Bootstrapper.dll"
    Bootstrapper64Path = "$rl\Loader\x64\Bootstrapper\Reloaded.Mod.Loader.Bootstrapper.dll"
    ApplicationConfigDirectory = "$rl\Apps"; ModUserConfigDirectory = "$rl\User\Mods"; MiscConfigDirectory = "$rl\User\Misc"
    PluginConfigDirectory = "$rl\Plugins"; ModConfigDirectory = "$rl\Mods"; EnabledPlugins = @()
    LanguageFile = "$rl\Assets\Languages\ru-RU.xaml"; ThemeFile = "$rl\Theme\Default.xaml"
    FirstLaunch = $false; ShowConsole = $false; LoaderSetupTimeout = 120000
} | ConvertTo-Json -Depth 4 | Set-Content $rj -Encoding UTF8

# 6. Launchers (cmd reads .bat files in the OEM code page).
$launch = "start `"`" `"%~dp0Reloaded-II\Reloaded-II.exe`" --launch `"$exe`""
@"
@echo off
title P3R Co-op - host
cd /d "%~dp0"
echo Which save slot do you play co-op on? (its number in the game's Load menu, e.g. 7)
set /p SLOT=Slot:
start "P3R Co-op link - host" "%~dp0Link\P3RCoopLink.exe" host --save %SLOT%
$launch
"@ | Set-Content (Join-Path $dest 'Host co-op.bat') -Encoding Oem
@"
@echo off
title P3R Co-op - guest
cd /d "%~dp0"
echo Host IP address - or just press Enter to find the host on your LAN / VPN:
set /p IP=IP:
start "P3R Co-op link - guest" "%~dp0Link\P3RCoopLink.exe" join %IP%
$launch
"@ | Set-Content (Join-Path $dest 'Join co-op.bat') -Encoding Oem

$desktop = [Environment]::GetFolderPath('Desktop')
if ($env:P3RCOOP_KIT_TEST) { $desktop = Join-Path $dest 'desktop-test'; New-Item -ItemType Directory -Force $desktop | Out-Null }
$ws = New-Object -ComObject WScript.Shell
foreach ($name in 'Host co-op', 'Join co-op') {
    $lnk = $ws.CreateShortcut((Join-Path $desktop "P3R $name.lnk"))
    $lnk.TargetPath = Join-Path $dest "$name.bat"
    $lnk.WorkingDirectory = $dest
    $lnk.IconLocation = "$exe,0"
    $lnk.Save()
}

Write-Host ''
Write-Host 'Done! Desktop shortcuts: "P3R Join co-op" (you) / "P3R Host co-op" (the friend who hosts).' -ForegroundColor Green
Write-Host "Installed to $dest"
Done 0
