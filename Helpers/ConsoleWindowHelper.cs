using System.Runtime.InteropServices;

namespace mykeepass.Helpers;

/// <summary>
/// Moves and optionally resizes the console/terminal OS window using Win32 APIs.
/// </summary>
internal static class ConsoleWindowHelper
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint SWP_NOSIZE    = 0x0001;  // keep current size
    private const uint SWP_NOZORDER  = 0x0004;  // keep z-order
    private const int  SM_CXSCREEN   = 0;
    private const int  SM_CYSCREEN   = 1;

    /// <summary>
    /// Centers the console window on the primary monitor without changing its size.
    /// Safe to call at any point; silently does nothing if the handle is unavailable.
    /// </summary>
    public static void CenterOnScreen()
    {
        IntPtr hWnd = GetConsoleWindow();
        if (hWnd == IntPtr.Zero) return;

        if (!GetWindowRect(hWnd, out RECT rect)) return;

        int winW    = rect.Right  - rect.Left;
        int winH    = rect.Bottom - rect.Top;
        int screenW = GetSystemMetrics(SM_CXSCREEN);
        int screenH = GetSystemMetrics(SM_CYSCREEN);

        int x = Math.Max(0, (screenW - winW) / 2);
        int y = Math.Max(0, (screenH - winH) / 2);

        SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
    }
}
