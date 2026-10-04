# Screenshot of the P3R game window via the desktop (GDI window capture of DX games comes out black).
# Picks the window titled "P3R" (not UE4SS's "UE4SS Debugging Tools" window in the same process), minimizes the
# UE4SS window, brings the game to the front, then grabs its rect.
# Usage: powershell -File shot.ps1 <out.png> [scale] [-NoFocus]
param([string]$Out = "shot.png", [double]$Scale = 0.33, [switch]$NoFocus)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public class GW {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public static List<KeyValuePair<IntPtr,string>> Windows(uint pid) {
    var r = new List<KeyValuePair<IntPtr,string>>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); r.Add(new KeyValuePair<IntPtr,string>(h, sb.ToString())); }
      return true; }, IntPtr.Zero);
    return r; } }
"@
[GW]::SetProcessDPIAware() | Out-Null
$proc = Get-Process -Name P3R -ErrorAction Stop | Select-Object -First 1
$wins = [GW]::Windows([uint32]$proc.Id)
foreach ($w in $wins) { if ($w.Value -like "UE4SS*") { [GW]::ShowWindow($w.Key, 6) | Out-Null } }   # SW_MINIMIZE
$game = $wins | Where-Object { $_.Value -like "P3R*" } | Select-Object -First 1
if (-not $game) { "no P3R game window (windows: $($wins.Value -join ' | '))"; exit 1 }
if (-not $NoFocus) { [GW]::ShowWindow($game.Key, 9) | Out-Null; [GW]::SetForegroundWindow($game.Key) | Out-Null; Start-Sleep -Milliseconds 400 }
$r = New-Object GW+RECT
[GW]::GetWindowRect($game.Key, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
$sw = [int]($w * $Scale); $sh = [int]($h * $Scale)
$small = New-Object System.Drawing.Bitmap $bmp, $sw, $sh
$small.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"$Out ${w}x${h} -> ${sw}x${sh}"
