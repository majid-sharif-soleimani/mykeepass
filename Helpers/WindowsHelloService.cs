using System.Text;
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
    /// Shows the "Making sure it's you" Windows Security dialog.
    /// Returns true only when the user successfully verifies (PIN / biometric).
    /// </summary>
    public static async Task<bool> VerifyAsync(string message)
    {
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
