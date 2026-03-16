using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation;
using Windows.Security.Credentials;
using Windows.Security.Credentials.UI;

namespace mykeepass.Helpers;

/// <summary>
/// Wraps Windows Hello (UserConsentVerifier) and the Windows Credential Vault
/// (PasswordVault) so the app can:
///   1. Verify the user's identity with PIN / fingerprint / face.
///   2. Store and retrieve the KeePass master password, protected by the
///      user's Windows account (DPAPI-backed, never written to disk in plain text).
/// </summary>
internal static class WindowsHelloService
{
    private const string VaultResource = "MyKeePass";

    // ── IUserConsentVerifierInterop ────────────────────────────────────────────
    // Win32 apps must use this COM interop interface instead of the plain WinRT
    // static method so they can pass an owner HWND.  Windows then shows the
    // "Making sure it's you" dialog in front of (owned by) that window.
    // GUID: 39E050C3-4E74-441A-8DC0-B81104DF949C  (userconsentverifierinterop.h)

    [Guid("39E050C3-4E74-441A-8DC0-B81104DF949C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    [ComImport]
    private interface IUserConsentVerifierInterop
    {
        // Maps to: HRESULT RequestVerificationForWindowAsync(HWND, HSTRING, REFIID, void**)
        void RequestVerificationForWindowAsync(
            IntPtr                                    appWindow,
            [MarshalAs(UnmanagedType.HString)] string message,
            ref Guid                                  riid,
            out IntPtr                                asyncOperation);
    }

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,   // HSTRING
        ref Guid riid,
        out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string? src,
        uint length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    // ── Availability ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when Windows Hello is set up and ready on this device.
    /// </summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var status = await UserConsentVerifier.CheckAvailabilityAsync();
            return status == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    // ── Identity verification ─────────────────────────────────────────────────

    /// <summary>
    /// Shows the "Making sure it's you" Windows Security dialog owned by
    /// <paramref name="ownerHwnd"/>, so the dialog appears in front of the
    /// caller's window rather than behind it.
    /// Falls back to the owner-less WinRT API if the interop path fails.
    /// Returns true only when the user successfully verifies (PIN / biometric).
    /// </summary>
    public static async Task<bool> VerifyAsync(IntPtr ownerHwnd, string message)
    {
        if (ownerHwnd != IntPtr.Zero)
        {
            try
            {
                const string classId =
                    "Windows.Security.Credentials.UI.UserConsentVerifier";

                int hr = WindowsCreateString(classId, (uint)classId.Length,
                                             out IntPtr hsClassId);
                if (hr >= 0)
                {
                    try
                    {
                        Guid interopIid = typeof(IUserConsentVerifierInterop).GUID;
                        hr = RoGetActivationFactory(hsClassId, ref interopIid,
                                                    out IntPtr pFactory);
                        if (hr >= 0)
                        {
                            try
                            {
                                var interop = (IUserConsentVerifierInterop)
                                    Marshal.GetObjectForIUnknown(pFactory);

                                Guid asyncIid =
                                    typeof(IAsyncOperation<UserConsentVerificationResult>)
                                    .GUID;

                                interop.RequestVerificationForWindowAsync(
                                    ownerHwnd, message, ref asyncIid, out IntPtr asyncPtr);

                                try
                                {
                                    var asyncOp =
                                        (IAsyncOperation<UserConsentVerificationResult>)
                                        Marshal.GetObjectForIUnknown(asyncPtr);

                                    var result = await asyncOp;
                                    return result == UserConsentVerificationResult.Verified;
                                }
                                finally { Marshal.Release(asyncPtr); }
                            }
                            finally { Marshal.Release(pFactory); }
                        }
                    }
                    finally { WindowsDeleteString(hsClassId); }
                }
            }
            catch { /* fall through to the owner-less API below */ }
        }

        // Fallback: no owner HWND or interop failed — use the plain WinRT static.
        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            return result == UserConsentVerificationResult.Verified;
        }
        catch
        {
            return false;
        }
    }

    // ── Credential vault ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when a master password for <paramref name="databaseName"/>
    /// is already stored in the Windows Credential Vault.
    /// </summary>
    public static bool HasStoredPassword(string databaseName)
    {
        try
        {
            new PasswordVault().Retrieve(VaultResource, databaseName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Retrieves the stored master password as a UTF-8 byte array.
    /// The Windows API string is kept local to this method and becomes
    /// GC-eligible immediately on return.
    /// The <b>caller</b> must zero-fill the returned array after use.
    /// Returns <c>null</c> if no credential is found.
    /// </summary>
    public static byte[]? RetrievePasswordAsBytes(string databaseName)
    {
        try
        {
            var cred = new PasswordVault().Retrieve(VaultResource, databaseName);
            cred.RetrievePassword();
            // Convert to bytes immediately; the string becomes unreachable here.
            return Encoding.UTF8.GetBytes(cred.Password);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Retrieves the stored master password as a plain string.
    /// Prefer <see cref="RetrievePasswordAsBytes"/> for new code.
    /// Returns <c>null</c> if no credential is found.
    /// </summary>
    public static string? RetrievePassword(string databaseName)
    {
        try
        {
            var cred = new PasswordVault().Retrieve(VaultResource, databaseName);
            cred.RetrievePassword();
            return cred.Password;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves (or overwrites) the master password in the Windows Credential Vault.
    /// The vault is DPAPI-protected — only the current Windows user can read it.
    /// </summary>
    public static void StorePassword(string databaseName, string password)
    {
        try
        {
            var vault = new PasswordVault();
            // Remove any stale entry first so we don't accumulate duplicates.
            try { vault.Remove(vault.Retrieve(VaultResource, databaseName)); } catch { }
            vault.Add(new PasswordCredential(VaultResource, databaseName, password));
        }
        catch
        {
            // Non-fatal — the app can still work without storing the password.
        }
    }

    /// <summary>
    /// Deletes any stored credential for <paramref name="databaseName"/>.
    /// Called when the stored password turns out to be wrong (e.g. user changed
    /// the master password) so we don't loop forever with bad credentials.
    /// </summary>
    public static void RemoveStoredPassword(string databaseName)
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(VaultResource, databaseName));
        }
        catch { }
    }
}
