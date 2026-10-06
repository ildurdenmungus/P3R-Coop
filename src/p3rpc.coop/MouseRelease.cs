using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace p3rpc.coop;

/// <summary>
/// The co-op setup keeps the game running in the background (p3rpc.essentials RenderInBackground: a paused game
/// would stall the partner). The game then still thinks it has the mouse and keeps it locked in the middle of the
/// screen even while another window is in front (user, 2026-10-07). P3R.exe's imports of user32 SetCursorPos /
/// ClipCursor are redirected here: while P3R is not the foreground window they do nothing (ClipCursor releases).
/// </summary>
public static unsafe class MouseRelease
{
    private static delegate* unmanaged[Stdcall]<int, int, int> _setCursorPos;
    private static delegate* unmanaged[Stdcall]<void*, int> _clipCursor;
    private static uint _pid;

    public static void Install(CoopLog log)
    {
        var exe = (byte*)Process.GetCurrentProcess().MainModule!.BaseAddress;
        _pid = (uint)Environment.ProcessId;
        var set = ImportSlots.Find(exe, "user32.dll", "SetCursorPos");
        var clip = ImportSlots.Find(exe, "user32.dll", "ClipCursor");
        if (set != null)
        {
            _setCursorPos = (delegate* unmanaged[Stdcall]<int, int, int>)*set;
            delegate* unmanaged[Stdcall]<int, int, int> h = &SetCursorPosHook;
            ImportSlots.Patch(set, (nint)h);
        }
        if (clip != null)
        {
            _clipCursor = (delegate* unmanaged[Stdcall]<void*, int>)*clip;
            delegate* unmanaged[Stdcall]<void*, int> h = &ClipCursorHook;
            ImportSlots.Patch(clip, (nint)h);
        }
        log.Info($"Mouse release on (SetCursorPos {(set != null ? "hooked" : "not imported")}, ClipCursor {(clip != null ? "hooked" : "not imported")})");
    }

    private static bool GameInFront()
    {
        var w = GetForegroundWindow();
        if (w == 0) return false;
        GetWindowThreadProcessId(w, out var pid);
        return pid == _pid;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetCursorPosHook(int x, int y) => GameInFront() ? _setCursorPos(x, y) : 1;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ClipCursorHook(void* rect) => _clipCursor(GameInFront() ? rect : null);

    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
