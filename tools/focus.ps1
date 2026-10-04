# Bring the P3R game window (title "P3R", not UE4SS's debug window) to the foreground; um win drive only types
# into the foreground window. Prints the user's idle seconds so callers can avoid fighting a human for focus.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class F {
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] struct LII { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LII p);
  public static double Idle() { var l = new LII(); l.cbSize = 8; GetLastInputInfo(ref l); return (Environment.TickCount - l.dwTime) / 1000.0; }
  public static IntPtr Game(uint pid) { IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { var sb = new StringBuilder(64); GetWindowText(h, sb, 64);
        if (sb.ToString().StartsWith("P3R")) { found = h; return false; } } return true; }, IntPtr.Zero);
    return found; } }
"@
$p = Get-Process -Name P3R -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { "no-game"; exit 1 }
$h = [F]::Game([uint32]$p.Id)
if ($h -eq [IntPtr]::Zero) { "no-window"; exit 1 }
[F]::ShowWindow($h, 9) | Out-Null
[F]::SetForegroundWindow($h) | Out-Null
"focused idle={0:N0}" -f [F]::Idle()
