using System.Text.Json.Serialization;

namespace mykeepass.Models;

public sealed class DatabaseConfig
{
    /// <summary>
    /// Google Drive folder path + filename combined, e.g. "Keepass/database.kdbx".
    /// Use just the filename when the file lives at Drive root: "database.kdbx".
    /// </summary>
    public string File { get; set; } = string.Empty;

    /// <summary>
    /// Optional KeePass key file on Google Drive, same format as <see cref="File"/>,
    /// e.g. "Keepass/database.keyx". Leave null for password-only databases.
    /// </summary>
    public string? KeyFile { get; set; }

    // ── Helpers (not serialized) ──────────────────────────────────────────────

    [JsonIgnore]
    public string FileName   => Path.GetFileName(File);

    [JsonIgnore]
    public string FolderPath => Path.GetDirectoryName(File)?.Replace('\\', '/') ?? string.Empty;

    [JsonIgnore]
    public string? KeyFileName   => KeyFile is null ? null : Path.GetFileName(KeyFile);

    [JsonIgnore]
    public string? KeyFolderPath => KeyFile is null ? null
        : (Path.GetDirectoryName(KeyFile)?.Replace('\\', '/') ?? string.Empty);
}
