using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace mykeepass.Helpers;

/// <summary>
/// Copies sensitive text to the clipboard while:
///   • Excluding the data from Windows clipboard history (Win 10/11).
///   • Automatically clearing the clipboard after a configurable timeout.
/// </summary>
public static class ClipboardHelper
{
    private static CancellationTokenSource? _clearCts;

    /// <summary>
    /// Sets <paramref name="text"/> as the clipboard contents, suppresses clipboard-history
    /// recording on Windows, and schedules a secure clear after
    /// <paramref name="clearAfterSeconds"/> seconds (default 60).
    /// Cancels any pending clear from a previous call.
    /// </summary>
    public static void SetSecureText(string text, int clearAfterSeconds = 60)
    {
        // Cancel any pending auto-clear from a previous copy.
        _clearCts?.Cancel();
        _clearCts?.Dispose();
        _clearCts = new CancellationTokenSource();

        if (OperatingSystem.IsWindows())
            SetClipboardExcludeHistory(text);
        else
            TextCopy.ClipboardService.SetText(text);

        // Schedule the clear on a background thread; cancels if a new copy fires first.
        var cts = _clearCts;
        _ = Task.Delay(TimeSpan.FromSeconds(clearAfterSeconds), cts.Token)
            .ContinueWith(t =>
            {
                if (!t.IsCanceled)
                    ClearClipboard();
            }, TaskScheduler.Default);
    }

    /// <summary>Immediately empties the clipboard.</summary>
    public static void ClearClipboard()
    {
        // Cancel any scheduled auto-clear — we're doing it now.
        _clearCts?.Cancel();

        if (OperatingSystem.IsWindows())
            ClearClipboardWin32();
        else
            TextCopy.ClipboardService.SetText(string.Empty);
    }

    // ── Windows implementation ────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static void SetClipboardExcludeHistory(string text)
    {
        const uint GMEM_MOVEABLE  = 0x0002;
        const uint CF_UNICODETEXT = 13;

        // This sentinel format, when present in the clipboard, tells the Windows
        // clipboard history manager to exclude this update from history.
        uint excludeFormat = RegisterClipboardFormat(
            "ExcludeClipboardContentFromMonitorProcessing");

        if (!OpenClipboard(IntPtr.Zero))
        {
            // Fall back to TextCopy if we cannot open the clipboard.
            TextCopy.ClipboardService.SetText(text);
            return;
        }

        try
        {
            EmptyClipboard();

            // Allocate a global memory block for the Unicode text (null-terminated).
            byte[] bytes   = System.Text.Encoding.Unicode.GetBytes(text + "\0");
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes.Length);
            if (hGlobal != IntPtr.Zero)
            {
                IntPtr ptr = GlobalLock(hGlobal);
                if (ptr != IntPtr.Zero)
                {
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);
                    GlobalUnlock(hGlobal);
                }
                // Ownership of hGlobal passes to the OS after SetClipboardData —
                // do NOT call GlobalFree on it.
                SetClipboardData(CF_UNICODETEXT, hGlobal);
            }

            // Setting the exclusion format with a null handle is the documented way
            // to suppress clipboard-history recording (see Windows SDK docs).
            if (excludeFormat != 0)
                SetClipboardData(excludeFormat, IntPtr.Zero);
        }
        finally
        {
            CloseClipboard();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ClearClipboardWin32()
    {
        if (OpenClipboard(IntPtr.Zero))
        {
            try   { EmptyClipboard(); }
            finally { CloseClipboard(); }
        }
    }

    // ── P/Invoke declarations ─────────────────────────────────────────────────

    [DllImport("user32.dll",   SetLastError = true)]
    private static extern bool   OpenClipboard(IntPtr hWnd);

    [DllImport("user32.dll",   SetLastError = true)]
    private static extern bool   EmptyClipboard();

    [DllImport("user32.dll",   SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll",   SetLastError = true)]
    private static extern bool   CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool   GlobalUnlock(IntPtr hMem);

    [DllImport("user32.dll",   CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint   RegisterClipboardFormat(string lpszFormat);
}
