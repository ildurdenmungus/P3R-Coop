# One-PC test of P3RCoopLink's TCP battle-command relay: a host link (run\) and a guest link (run_guest\) over
# 127.0.0.1. Each side appends a command to its local_cmds.txt; it must show up in the other side's
# remote_cmds.txt. Leaves both links running (stop them by PID afterwards).
$root = "C:\Users\mbasd\P3R-Coop"
$exe = "$root\bin\link\P3RCoopLink.exe"
$hostDir = "$root\run"
$guestDir = "$root\run_guest"
New-Item -ItemType Directory -Force $guestDir | Out-Null
foreach ($f in @("$hostDir\remote_cmds.txt", "$guestDir\remote_cmds.txt", "$guestDir\local_cmds.txt")) {
    if (Test-Path $f) { Clear-Content $f }
}

$hostProc = Start-Process cmd -PassThru -WindowStyle Hidden -ArgumentList "/c set P3RCOOP_DIR=$hostDir&& `"$exe`" host > `"$hostDir\host_console.txt`" 2>&1"
Start-Sleep 2
$joinProc = Start-Process cmd -PassThru -WindowStyle Hidden -ArgumentList "/c set P3RCOOP_DIR=$guestDir&& `"$exe`" join 127.0.0.1 > `"$guestDir\join_console.txt`" 2>&1"
Start-Sleep 4

Add-Content "$hostDir\local_cmds.txt" "7 3 P0 1 0 E0"
Add-Content "$guestDir\local_cmds.txt" "7 4 P3 2 24 E0,E1"
Start-Sleep 2

"--- host console";  Get-Content "$hostDir\host_console.txt"
"--- guest console"; Get-Content "$guestDir\join_console.txt"
"--- host received (remote_cmds)";  Get-Content "$hostDir\remote_cmds.txt" -ErrorAction SilentlyContinue
"--- guest received (remote_cmds)"; Get-Content "$guestDir\remote_cmds.txt" -ErrorAction SilentlyContinue
"--- link processes"; Get-Process P3RCoopLink | Select-Object Id, StartTime | Format-Table -AutoSize | Out-String
