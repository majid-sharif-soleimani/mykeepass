namespace mykeepass.Models;

/// <summary>
/// Application configuration read from appsettings.json.
/// If the file is absent or incomplete the user is prompted on first run.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Filename of the KeePass database in Google Drive (e.g. "passwords.kdbx").
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Optional slash-separated folder path within Google Drive
    /// (e.g. "Backups/KeePass"). Leave empty to search the entire Drive root.
    /// </summary>
    public string DatabasePath { get; set; } = string.Empty;
}
