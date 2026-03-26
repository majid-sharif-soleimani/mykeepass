using System.Text.Json.Serialization;

namespace mykeepass.Models;

/// <summary>
/// Application configuration read from appsettings.json.
/// Supports both the legacy flat format and the new multi-account format.
/// </summary>
public sealed class AppConfig
{
    // ── New multi-account format ──────────────────────────────────────────────

    public List<AccountConfig>? Accounts { get; set; }

    // ── Legacy fields — kept nullable for migration detection only ────────────

    /// <summary>Legacy: filename of the KeePass database in Google Drive.</summary>
    public string? DatabaseName { get; set; }

    /// <summary>Legacy: optional slash-separated folder path within Google Drive.</summary>
    public string? DatabasePath { get; set; }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the file uses the old flat format (no Accounts array, but
    /// DatabaseName is present). Triggers auto-migration on first load.
    /// </summary>
    [JsonIgnore]
    public bool IsLegacyFormat =>
        Accounts is null && !string.IsNullOrWhiteSpace(DatabaseName);

    /// <summary>
    /// True when the config is usable: Accounts is non-empty and every account
    /// has an email and at least one database with a non-empty File path.
    /// </summary>
    [JsonIgnore]
    public bool IsValid =>
        Accounts is { Count: > 0 } &&
        Accounts.TrueForAll(a =>
            !string.IsNullOrWhiteSpace(a.Email) &&
            a.Databases is { Count: > 0 } &&
            a.Databases.TrueForAll(d => !string.IsNullOrWhiteSpace(d.File)));
}
