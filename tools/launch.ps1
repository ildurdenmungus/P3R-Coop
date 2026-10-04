# Launch P3R through Reloaded-II and retry when it stalls in mod loading (seen after a previous instance crashed
# on exit: the process sits at ~50 MB with no "P3R" window). Kills only the exact stuck PID.
param([int]$Tries = 3)
if (Get-Process -Name P3R -ErrorAction SilentlyContinue) { "P3R is already running - not launching another copy"; exit 0 }
$reloaded = "C:\Users\mbasd\Reloaded-II\Reloaded-II.exe"
$exe = "D:\SteamLibrary\steamapps\common\P3R\P3R\Binaries\Win64\P3R.exe"
Get-Process -Name CrashReportClient -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Confirm:$false }
for ($t = 1; $t -le $Tries; $t++) {
    Start-Process $reloaded -ArgumentList "--launch `"$exe`""
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep 3
        $p = Get-Process -Name P3R -ErrorAction SilentlyContinue | Select-Object -First 1
        # UE4SS's debug window can be the "main" window, so judge by title OR by memory (a stuck loader sits at ~50 MB)
        if ($p -and ($p.MainWindowTitle -match "^(P3R|UE4SS)" -or $p.WorkingSet64 -gt 600MB)) { "try ${t}: running pid $($p.Id)"; exit 0 }
        if ($p -and $p.MainWindowTitle -like "Oh Noes*") {
            "try ${t}: crashed at startup (Oh Noes!), killing pid $($p.Id)"
            Stop-Process -Id $p.Id -Confirm:$false
            Get-Process -Name CrashReportClient -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Confirm:$false }
            Start-Sleep 4
            break
        }
        if ($p -and $i -ge 8 -and $p.WorkingSet64 -lt 200MB) {
            "try ${t}: stuck at $([int]($p.WorkingSet64/1MB)) MB, killing pid $($p.Id)"
            Stop-Process -Id $p.Id -Confirm:$false
            Start-Sleep 4
            break
        }
    }
}
"failed to start after $Tries tries"
exit 1
