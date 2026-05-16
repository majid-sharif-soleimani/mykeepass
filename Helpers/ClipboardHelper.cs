using System.Diagnostics;
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
            SetClipboardPortable(text);

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

        try
        {
            if (OperatingSystem.IsWindows())
                ClearClipboardWin32();
            else
                ClearClipboardPortable();
        }
        catch (ClipboardUnavailableException)
        {
            // Copy failures are reported to the user at copy time. A later clear
            // failure should not crash the app during exit or background cleanup.
        }
    }

    // ── Portable implementation ───────────────────────────────────────────────

    private static void SetClipboardPortable(string text)
    {
        if (OperatingSystem.IsLinux())
        {
            if (IsCommandAvailable("wl-copy") && RunClipboardCommand("wl-copy", string.Empty, text))
                return;

            if (IsCommandAvailable("xclip") && RunClipboardCommand("xclip", "-selection clipboard", text))
                return;

            if (IsCommandAvailable("xsel") && RunClipboardCommand("xsel", "--clipboard --input", text))
                return;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (IsCommandAvailable("pbcopy") && RunClipboardCommand("pbcopy", string.Empty, text))
                return;
        }

        try
        {
            TextCopy.ClipboardService.SetText(text);
            return;
        }
        catch (Exception ex)
        {
            throw CreateClipboardUnavailableException(ex);
        }
    }

    private static void ClearClipboardPortable()
    {
        if (OperatingSystem.IsLinux())
        {
            if (IsCommandAvailable("wl-copy") && RunClipboardCommand("wl-copy", "--clear", null))
                return;

            if (IsCommandAvailable("xclip") && RunClipboardCommand("xclip", "-selection clipboard", string.Empty))
                return;

            if (IsCommandAvailable("xsel") && RunClipboardCommand("xsel", "--clipboard --clear", null))
                return;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (IsCommandAvailable("pbcopy") && RunClipboardCommand("pbcopy", string.Empty, string.Empty))
                return;
        }

        try
        {
            TextCopy.ClipboardService.SetText(string.Empty);
        }
        catch (Exception ex)
        {
            throw CreateClipboardUnavailableException(ex);
        }
    }

    private static ClipboardUnavailableException CreateClipboardUnavailableException(Exception? inner = null)
    {
        string message = OperatingSystem.IsLinux()
            ? "Clipboard unavailable. Install wl-clipboard for Wayland or xclip/xsel for X11."
            : "Clipboard unavailable on this platform.";

        return new ClipboardUnavailableException(message, inner);
    }

    private static bool RunClipboardCommand(string fileName, string arguments, string? stdin)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = fileName,
                    Arguments              = arguments,
                    RedirectStandardInput  = stdin is not null,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                }
            };

            process.Start();
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return false;

        foreach (string dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            string candidate = Path.Combine(dir, command);
            if (File.Exists(candidate)) return true;
        }

        return false;
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

public sealed class ClipboardUnavailableException : InvalidOperationException
{
    public ClipboardUnavailableException(string message, Exception? inner = null)
        : base(message, inner) { }
}
